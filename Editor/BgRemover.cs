using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImprimePlus.Editor;

/// <summary>
/// Quita-fondo offline con U²-Net (u2netp.onnx). Replica el preprocesado de rembg:
/// resize 320×320, normalización por el máximo + (mean/std) ImageNet, inferencia,
/// y la máscara resultante se reescala y se usa como canal alfa de la imagen original.
/// </summary>
public sealed class BgRemover : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };
    private const int N = 320;

    public BgRemover(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First(); // d0 = máscara principal
    }

    /// <summary>
    /// <paramref name="fullBgra"/>: pixeles BGRA de la imagen original (w×h).
    /// <paramref name="in320Bgra"/>: la misma imagen ya reescalada a 320×320 (BGRA).
    /// Devuelve BGRA premultiplicado con alfa = máscara (recorte).
    /// </summary>
    public byte[] Run(byte[] fullBgra, int w, int h, byte[] in320Bgra)
    {
        // ----- preprocesado -----
        float maxVal = 1f;
        for (int i = 0; i < in320Bgra.Length; i += 4)
        {
            maxVal = Math.Max(maxVal, in320Bgra[i]);     // B
            maxVal = Math.Max(maxVal, in320Bgra[i + 1]); // G
            maxVal = Math.Max(maxVal, in320Bgra[i + 2]); // R
        }

        var input = new DenseTensor<float>(new[] { 1, 3, N, N });
        for (int y = 0; y < N; y++)
        {
            for (int x = 0; x < N; x++)
            {
                int i = (y * N + x) * 4;
                float r = in320Bgra[i + 2] / maxVal;
                float g = in320Bgra[i + 1] / maxVal;
                float b = in320Bgra[i] / maxVal;
                input[0, 0, y, x] = (r - Mean[0]) / Std[0];
                input[0, 1, y, x] = (g - Mean[1]) / Std[1];
                input[0, 2, y, x] = (b - Mean[2]) / Std[2];
            }
        }

        // ----- inferencia -----
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        var mask = results.First(r => r.Name == _outputName).AsTensor<float>();

        // normalizar máscara a 0..1
        float mi = float.MaxValue, ma = float.MinValue;
        var flat = new float[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float v = mask[0, 0, y, x];
                flat[y * N + x] = v;
                if (v < mi) mi = v;
                if (v > ma) ma = v;
            }
        float range = (ma - mi) <= 1e-6f ? 1f : (ma - mi);

        // ----- aplicar máscara (bilinear) como alfa, premultiplicado -----
        var outBgra = new byte[fullBgra.Length];
        for (int y = 0; y < h; y++)
        {
            float my = (y + 0.5f) * N / h - 0.5f;
            int y0 = Math.Clamp((int)Math.Floor(my), 0, N - 1);
            int y1 = Math.Min(y0 + 1, N - 1);
            float fy = Math.Clamp(my - y0, 0, 1);
            for (int x = 0; x < w; x++)
            {
                float mx = (x + 0.5f) * N / w - 0.5f;
                int x0 = Math.Clamp((int)Math.Floor(mx), 0, N - 1);
                int x1 = Math.Min(x0 + 1, N - 1);
                float fx = Math.Clamp(mx - x0, 0, 1);

                float v00 = flat[y0 * N + x0], v10 = flat[y0 * N + x1];
                float v01 = flat[y1 * N + x0], v11 = flat[y1 * N + x1];
                float top = v00 + (v10 - v00) * fx;
                float bot = v01 + (v11 - v01) * fx;
                float v = top + (bot - top) * fy;
                float alpha01 = Math.Clamp((v - mi) / range, 0f, 1f);
                byte a = (byte)(alpha01 * 255f);

                int i = (y * w + x) * 4;
                // premultiplicar para que Win2D lo componga sin halos
                outBgra[i] = (byte)(fullBgra[i] * a / 255);
                outBgra[i + 1] = (byte)(fullBgra[i + 1] * a / 255);
                outBgra[i + 2] = (byte)(fullBgra[i + 2] * a / 255);
                outBgra[i + 3] = a;
            }
        }
        return outBgra;
    }

    public void Dispose() => _session.Dispose();
}
