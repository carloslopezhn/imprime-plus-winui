# Imprime+

**Editor de impresión de imágenes para escritorio** — Diseñá layouts profesionales de fotos y enviálos directamente a tu impresora. Versión nativa de Windows construida con **WinUI 3 / .NET 8**.

[![Versión](https://img.shields.io/badge/versión-2.3.0-blue.svg)](https://imprime.utp.hn)
[![WinUI](https://img.shields.io/badge/WinUI-3-purple.svg)](https://learn.microsoft.com/windows/apps/winui/winui3/)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4.svg)](https://dotnet.microsoft.com/)
[![Plataforma](https://img.shields.io/badge/plataforma-Windows%2010%2F11-lightgrey.svg)]()
[![Licencia](https://img.shields.io/badge/licencia-MIT-green.svg)](LICENSE)

---

## Descripción

**Imprime+** es una aplicación de escritorio nativa para Windows que permite importar múltiples imágenes, organizarlas en layouts configurables, aplicar estilos visuales y enviarlas a imprimir directamente desde la aplicación. Renderizado acelerado por GPU con **Win2D**, quita-fondo offline con **ONNX Runtime**, y actualizaciones automáticas a través de un servidor propio.

**Ideal para:** estudios fotográficos, imprentas, fotógrafos de eventos, y cualquier persona que necesite imprimir fotos con layouts personalizados.

---

## Descarga

**[Descargar Imprime+ para Windows](https://imprime.utp.hn)**

App nativa de escritorio · self-contained (no requiere instalar el Windows App Runtime aparte) · Windows 10/11 · 64-bit.

---

## Novedades de la versión 2.3.0

- **Estabilidad** — Se eliminaron los cierres inesperados al **agregar imágenes** o **arrastrar archivos** al lienzo en algunas computadoras (por ejemplo, cuando la carpeta *Imágenes* está redirigida a OneDrive o ausente). Ningún fallo del selector de archivos cierra ya la aplicación.
- **Red de seguridad global** — Cualquier error aislado se registra en `%TEMP%\ImprimePlus\crash.log` y la app permanece abierta, sin perder tu trabajo.
- _(2.2.9)_ Arreglado: el botón **Comprimido** cerraba la app al abrirlo.

---

## Características

### Gestión de imágenes
- **Arrastrar y soltar** imágenes directamente al editor
- **Pegar desde portapapeles** con `Ctrl+V` (incluido pegado remoto)
- **Copiar / pegar entre celdas** conservando el estilo completo
- **Importación masiva** de múltiples imágenes
- **Archivos comprimidos** ZIP, RAR, 7Z, TAR, TAR.GZ, TAR.BZ2, TAR.XZ — extrae y carga todas las imágenes de una vez (incluido anidamiento)
- **Selección múltiple** con `Ctrl+Clic`
- **Inspector individual** por imagen: zoom interno, posición dentro de la celda, título, forma, borde, filtros
- **Orientación EXIF** respetada y gestión de color a sRGB al cargar

### Modos de distribución

| Modo | Descripción |
|------|-------------|
| **Cuadrícula** | Define filas × columnas manualmente |
| **Cantidad** | Indica cuántas imágenes por página y el motor calcula la cuadrícula óptima |
| **Tamaño** | Define dimensiones exactas (ancho × alto) y las imágenes fluyen automáticamente |

### Modo Póster
- Divide una sola imagen en múltiples páginas
- Vista previa en tiempo real con cuadrícula y numeración de páginas
- Impresión directa de todas las páginas del póster

### Configuración de página
- **Presets incluidos:** Carta, Legal, Oficio, A4, A5, 4×6", 5×7"
- **Presets personalizados:** guardá y eliminá tus propios tamaños
- **Unidades:** centímetros, pulgadas, milímetros (márgenes y espaciado conscientes de unidad)
- **Orientación:** vertical / horizontal
- **Márgenes** independientes y **espaciado** horizontal/vertical

### Estilos de imagen
- **Formas:** rectángulo, redondeado, circular, hexágono, estrella
- Borde configurable (ancho y color), radio de esquinas, sombra
- Ajuste de imagen: **Cubrir, Contener, Estirar**
- Color de fondo por celda y alineación
- **Filtros:** brillo, contraste, saturación, escala de grises, sepia
- **Quita-fondo offline** con modelo U²-Net (ONNX) — sin enviar nada a internet

### Títulos
- Posición: debajo, arriba o superpuesto
- Título **global** con overrides individuales por imagen
- Fuente del texto: nombre del archivo (con/sin extensión), numeración automática o texto manual
- Color de texto y de fondo configurables

### Impresión y exportación
- Selección de impresora del sistema (locales y de red)
- Configuración de impresora nativa de Windows (recordada por impresora y sesión)
- Vista previa propia con ajuste al área imprimible
- Impresión nítida respetando orientación de la página
- **Exportación a PDF** con páginas, fecha y hora

### Actualizaciones
- **Verificación automática** al iniciar y botón manual
- Gestor de descarga con barra de progreso (MB, %, velocidad) y botón **Cancelar**
- Instalador tradicional (Inno Setup) con la versión en el nombre

---

## Stack tecnológico

| Tecnología | Uso |
|-----------|-----|
| .NET 8 (`net8.0-windows`) | Runtime y lenguaje (C#) |
| WinUI 3 / Windows App SDK | Interfaz nativa (unpackaged, self-contained) |
| Win2D (`Microsoft.Graphics.Win2D`) | Renderizado del lienzo acelerado por GPU |
| Microsoft.ML.OnnxRuntime + U²-Net | Quita-fondo offline |
| SharpCompress | Extracción de comprimidos (ZIP/RAR/7Z/TAR…) |
| PDFsharp | Exportación a PDF |
| CommunityToolkit.Mvvm | Patrón MVVM |
| Inno Setup | Instalador de escritorio |
| Python / Flask | Servidor de descargas y feed de actualización |

---

## Estructura del proyecto

```
imprime-plus-winui/
├── App.xaml(.cs)             # Aplicación + red de seguridad global de excepciones
├── MainWindow.xaml(.cs)      # Ventana con titlebar personalizada
├── MainPage.xaml(.cs)        # Editor principal (lienzo, toolbar, inspector)
├── Editor/
│   ├── ImageLoader.cs        # Carga a CanvasBitmap (EXIF + color sRGB)
│   └── Archives.cs           # Extracción de imágenes desde comprimidos
├── Models/                   # u2netp.onnx (quita-fondo) y modelos de datos
├── ViewModels/               # ViewModels (MVVM)
├── ImprimePlus.Core/         # Lógica de dominio reutilizable + tests
├── Assets/                   # Iconos, fuentes y recursos
├── installer.iss             # Instalador Inno Setup
└── ImprimePlus.csproj        # Proyecto principal (.NET 8 / WinUI 3)
```

---

## Desarrollo

### Requisitos previos
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Workload de Windows App SDK / WinUI 3
- Windows 10/11 (10.0.17763.0+)
- [Inno Setup 6](https://jrsoftware.org/isdl.php) (para compilar el instalador)

### Compilar y ejecutar

```powershell
dotnet build ImprimePlus.csproj -c Debug
dotnet run --project ImprimePlus.csproj
```

### Publicar instalador

```powershell
# 1. Publish self-contained x64
dotnet publish ImprimePlus.csproj -c Release -r win-x64 --self-contained -o publish

# 2. Compilar el instalador (genera installer-out\ImprimePlus-Setup-X.Y.Z.exe)
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
```

---

## Licencia

Este proyecto está bajo la licencia [MIT](LICENSE).

---

## Autores

**UTP Honduras** — [utp.hn](https://utp.hn)

---

<p align="center">Hecho con corazón en Honduras</p>
