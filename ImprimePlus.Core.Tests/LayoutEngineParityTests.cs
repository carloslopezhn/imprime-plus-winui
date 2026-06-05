using System.Text.Json;
using ImprimePlus.Core.Layout;

namespace ImprimePlus.Core.Tests;

/// <summary>
/// Paridad bit-a-bit (dentro de 1e-6) entre el LayoutEngine.cs portado y el
/// engine.js ORIGINAL del Imprime+ Tauri. El golden (engine_golden.json) lo
/// genera generate_golden.cjs ejecutando el engine.js real sobre 12 configs.
/// </summary>
public class LayoutEngineParityTests
{
    private const double Eps = 1e-6;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IEnumerable<object[]> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "engine_golden.json");
        var json = File.ReadAllText(path);
        var cases = JsonSerializer.Deserialize<List<GoldenCase>>(json, JsonOpts)!;
        foreach (var c in cases)
            yield return new object[] { c };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_engine_js(GoldenCase c)
    {
        var config = c.Config.ToConfig();
        var images = c.Images.Select(i => (ImageItem?)i.ToItem()).ToList();

        var layout = LayoutEngine.ComputeLayout(config);
        var pages = LayoutEngine.Paginate(images, layout);

        var e = c.ExpectedLayout;
        Assert.Equal(e.PageW, layout.PageW, Eps);
        Assert.Equal(e.PageH, layout.PageH, Eps);
        Assert.Equal(e.ContentW, layout.ContentW, Eps);
        Assert.Equal(e.ContentH, layout.ContentH, Eps);
        Assert.Equal(e.MarginTop, layout.MarginTop, Eps);
        Assert.Equal(e.MarginRight, layout.MarginRight, Eps);
        Assert.Equal(e.MarginBottom, layout.MarginBottom, Eps);
        Assert.Equal(e.MarginLeft, layout.MarginLeft, Eps);
        Assert.Equal(e.SpacingH, layout.SpacingH, Eps);
        Assert.Equal(e.SpacingV, layout.SpacingV, Eps);
        Assert.Equal(e.Cols, layout.Cols);
        Assert.Equal(e.Rows, layout.Rows);
        Assert.Equal(e.CellW, layout.CellW, Eps);
        Assert.Equal(e.CellH, layout.CellH, Eps);
        Assert.Equal(e.TotalSlots, layout.TotalSlots);
        Assert.Equal(e.PerPage, layout.PerPage);
        Assert.Equal(e.Unit, layout.Unit);

        // Paginación: misma cantidad de páginas y mismos ids por página.
        var actualPages = pages.Select(p => p.Images.Select(im => im.Id).ToList()).ToList();
        Assert.Equal(c.ExpectedPages.Count, actualPages.Count);
        for (int i = 0; i < c.ExpectedPages.Count; i++)
            Assert.Equal(c.ExpectedPages[i], actualPages[i]);
    }

    // ----- DTOs del golden -----

    public sealed class GoldenCase
    {
        public string Name { get; set; } = "";
        public ConfigDto Config { get; set; } = new();
        public List<ImageDto> Images { get; set; } = new();
        public LayoutDto ExpectedLayout { get; set; } = new();
        public List<List<string>> ExpectedPages { get; set; } = new();
        public override string ToString() => Name;
    }

    public sealed class ConfigDto
    {
        public string Unit { get; set; } = "cm";
        public double PageWidth { get; set; }
        public double PageHeight { get; set; }
        public double MarginTop { get; set; }
        public double MarginRight { get; set; }
        public double MarginBottom { get; set; }
        public double MarginLeft { get; set; }
        public double SpacingH { get; set; }
        public double SpacingV { get; set; }
        public string LayoutMode { get; set; } = "grid";
        public int GridCols { get; set; }
        public int GridRows { get; set; }
        public int CountPerPage { get; set; }
        public double ImgWidth { get; set; }
        public double ImgHeight { get; set; }

        public LayoutConfig ToConfig() => new()
        {
            Unit = Unit,
            PageWidth = PageWidth,
            PageHeight = PageHeight,
            MarginTop = MarginTop,
            MarginRight = MarginRight,
            MarginBottom = MarginBottom,
            MarginLeft = MarginLeft,
            SpacingH = SpacingH,
            SpacingV = SpacingV,
            LayoutMode = LayoutMode,
            GridCols = GridCols,
            GridRows = GridRows,
            CountPerPage = CountPerPage,
            ImgWidth = ImgWidth,
            ImgHeight = ImgHeight,
        };
    }

    public sealed class ImageDto
    {
        public string Id { get; set; } = "";
        public OverridesDto Overrides { get; set; } = new();
        public ImageItem ToItem() => new()
        {
            Id = Id,
            Overrides = new ImageOverrides { ColSpan = Overrides.ColSpan, RowSpan = Overrides.RowSpan },
        };
    }

    public sealed class OverridesDto
    {
        public int ColSpan { get; set; } = 1;
        public int RowSpan { get; set; } = 1;
    }

    public sealed class LayoutDto
    {
        public double PageW { get; set; }
        public double PageH { get; set; }
        public double ContentW { get; set; }
        public double ContentH { get; set; }
        public double MarginTop { get; set; }
        public double MarginRight { get; set; }
        public double MarginBottom { get; set; }
        public double MarginLeft { get; set; }
        public double SpacingH { get; set; }
        public double SpacingV { get; set; }
        public int Cols { get; set; }
        public int Rows { get; set; }
        public double CellW { get; set; }
        public double CellH { get; set; }
        public int TotalSlots { get; set; }
        public int PerPage { get; set; }
        public string Unit { get; set; } = "cm";
    }
}
