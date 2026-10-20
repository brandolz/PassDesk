using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Model;
using PassDesk.Crypto;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PassDesk.App.ViewModels;

/// <summary>Elemento de la lista de documentos (solo metadatos).</summary>
public sealed class DocumentItemViewModel(SecureDocument document) : ObservableObject
{
    public SecureDocument Document { get; } = document;

    public Guid Id => Document.Id;

    public string Title => Document.Title.Length > 0 ? Document.Title : Document.FileName;

    public string Subtitle =>
        $"{DocumentKinds.Describe(Document.Kind)} · {DocumentKinds.FormatSize(Document.Size)} · {Formatting.Date(Document.ModifiedUtc)}";

    public string Glyph => DocumentGlyphs.For(Document.Kind);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

internal static class DocumentGlyphs
{
    public static string For(DocumentKind kind) => kind switch
    {
        DocumentKind.Image => "",
        DocumentKind.Pdf => "",
        DocumentKind.Text => "",
        _ => "",
    };
}

public enum PreviewMode
{
    None,
    Loading,
    Image,
    Pdf,
    Text,
    Unsupported,
}

/// <summary>
/// Visor de documentos. El contenido se descifra en memoria fijada que se pone a cero en cuanto se ha
/// decodificado; nunca se escribe sin cifrar en disco. Al bloquear o cambiar de selección se descarta.
/// </summary>
public sealed class DocumentDetailViewModel(SecureDocumentService service) : ObservableObject
{
    private const int MaxPdfPages = 20;
    private const int MaxTextBytes = 1_000_000;
    private SecureDocument? _document;
    private int _loadVersion;
    private PreviewMode _mode;
    private ImageSource? _image;
    private string? _text;
    private string _status = string.Empty;
    private string _pdfInfo = string.Empty;

    public SecureDocument? Document => _document;

    public bool HasDocument => _document is not null;

    public string Title => _document is null ? string.Empty : (_document.Title.Length > 0 ? _document.Title : _document.FileName);

    public string Details => _document is null
        ? string.Empty
        : $"{DocumentKinds.Describe(_document.Kind)} · {DocumentKinds.FormatSize(_document.Size)} · {_document.FileName}";

    public string AddedText => _document is null ? string.Empty : "Añadido: " + Formatting.Date(_document.CreatedUtc);

    public string Glyph => _document is null ? string.Empty : DocumentGlyphs.For(_document.Kind);

    public PreviewMode Mode
    {
        get => _mode;
        private set
        {
            if (Set(ref _mode, value))
            {
                OnPropertiesChanged(nameof(IsLoading), nameof(IsImage), nameof(IsPdf), nameof(IsText), nameof(IsUnsupported));
            }
        }
    }

    public bool IsLoading => Mode == PreviewMode.Loading;

    public bool IsImage => Mode == PreviewMode.Image;

    public bool IsPdf => Mode == PreviewMode.Pdf;

    public bool IsText => Mode == PreviewMode.Text;

    public bool IsUnsupported => Mode == PreviewMode.Unsupported;

    public ImageSource? Image
    {
        get => _image;
        private set => Set(ref _image, value);
    }

    public ObservableCollection<ImageSource> PdfPages { get; } = [];

    public string PdfInfo
    {
        get => _pdfInfo;
        private set => Set(ref _pdfInfo, value);
    }

    public string? Text
    {
        get => _text;
        private set => Set(ref _text, value);
    }

    public string StatusMessage
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public async Task ShowAsync(SecureDocument? document)
    {
        int version = ++_loadVersion;
        ClearPreview();
        _document = document;
        OnPropertiesChanged(nameof(HasDocument), nameof(Title), nameof(Details), nameof(AddedText), nameof(Glyph));
        if (document is null)
        {
            Mode = PreviewMode.None;
            return;
        }

        Mode = PreviewMode.Loading;
        try
        {
            switch (document.Kind)
            {
                case DocumentKind.Image:
                    var image = LoadImage(document.Id);
                    if (version == _loadVersion)
                    {
                        Image = image;
                        Mode = PreviewMode.Image;
                    }

                    break;

                case DocumentKind.Pdf:
                    await LoadPdfAsync(document.Id, version);
                    if (version == _loadVersion)
                    {
                        Mode = PreviewMode.Pdf;
                    }

                    break;

                case DocumentKind.Text:
                    using (var content = service.Open(document.Id))
                    {
                        Text = Encoding.UTF8.GetString(content.Span[..Math.Min(content.Length, MaxTextBytes)]);
                    }

                    Mode = PreviewMode.Text;
                    break;

                default:
                    ShowUnsupported("No hay vista previa para este tipo de archivo. Puedes exportarlo para abrirlo con otra aplicación.");
                    break;
            }
        }
        catch (Exception ex) when (ex is VaultFormatException or IOException or NotSupportedException or COMException or FormatException
            or ArgumentException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        {
            AppLog.Warn(LogEvent.DocumentPreviewFailed);
            if (version == _loadVersion)
            {
                ClearPreview();
                ShowUnsupported(document.Kind switch
                {
                    DocumentKind.Image => "No se puede mostrar esta imagen (por ejemplo, fotos HEIC sin la extensión «HEIF» de Microsoft Store). Puedes exportarla.",
                    DocumentKind.Pdf => "No se puede mostrar este PDF (puede estar protegido con contraseña o dañado). Puedes exportarlo.",
                    _ => "No se pudo mostrar el documento. Puedes exportarlo.",
                });
            }
        }
    }

    public void Clear()
    {
        _loadVersion++;
        ClearPreview();
        _document = null;
        Mode = PreviewMode.None;
        OnPropertiesChanged(nameof(HasDocument), nameof(Title), nameof(Details), nameof(AddedText), nameof(Glyph));
    }

    private void ShowUnsupported(string message)
    {
        StatusMessage = message;
        Mode = PreviewMode.Unsupported;
    }

    private void ClearPreview()
    {
        Image = null;
        Text = null;
        PdfPages.Clear();
        PdfInfo = string.Empty;
        StatusMessage = string.Empty;
    }

    /// <summary>Decodifica la imagen desde memoria y corrige la orientación EXIF de las fotos del iPhone.</summary>
    private BitmapSource LoadImage(Guid id)
    {
        using var content = service.Open(id);
        using var stream = content.AsReadOnlyStream();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];

        double rotation = 0;
        try
        {
            if (decoder.Frames[0].Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort orientation)
            {
                rotation = orientation switch
                {
                    3 => 180,
                    6 => 90,
                    8 => 270,
                    _ => 0,
                };
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // Sin metadatos de orientación.
        }

        if (rotation != 0)
        {
            frame = new TransformedBitmap(frame, new RotateTransform(rotation));
        }

        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// Renderiza las páginas con el motor PDF integrado en Windows (Windows.Data.Pdf) en un hilo de fondo; a la
    /// interfaz solo llegan las imágenes de cada página. Las copias intermedias en memoria se ponen a cero.
    /// </summary>
    private async Task LoadPdfAsync(Guid id, int version)
    {
        byte[] copy;
        using (var content = service.Open(id))
        {
            copy = content.Span.ToArray();
        }

        (List<byte[]> Pages, uint Total) rendered;
        try
        {
            rendered = await Task.Run(() => RenderPdfPagesAsync(copy, MaxPdfPages));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(copy);
        }

        try
        {
            foreach (byte[] png in rendered.Pages)
            {
                if (version != _loadVersion)
                {
                    return;
                }

                PdfPages.Add(DecodePng(png));
            }
        }
        finally
        {
            foreach (byte[] png in rendered.Pages)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(png);
            }
        }

        uint shown = (uint)rendered.Pages.Count;
        PdfInfo = rendered.Total > shown
            ? $"Se muestran las primeras {shown} de {rendered.Total} páginas. Exporta el PDF para verlo completo."
            : Formatting.Count((int)rendered.Total, "página", "páginas");
    }

    private static BitmapImage DecodePng(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Devuelve cada página como PNG. Solo usa flujos nativos de WinRT y libera el documento al terminar.</summary>
    private static async Task<(List<byte[]> Pages, uint Total)> RenderPdfPagesAsync(byte[] pdfBytes, int maxPages)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(pdfBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        input.Seek(0);
        var pdf = await PdfDocument.LoadFromStreamAsync(input);
        try
        {
            uint total = pdf.PageCount;
            var pages = new List<byte[]>();
            for (uint index = 0; index < Math.Min(total, (uint)maxPages); index++)
            {
                using var page = pdf.GetPage(index);
                using var output = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(output, new PdfPageRenderOptions { DestinationWidth = 1400 });
                var png = new byte[output.Size];
                using (var reader = new DataReader(output.GetInputStreamAt(0)))
                {
                    await reader.LoadAsync((uint)png.Length);
                    reader.ReadBytes(png);
                }

                pages.Add(png);
            }

            return (pages, total);
        }
        finally
        {
            if (pdf is WinRT.IWinRTObject winrt)
            {
                winrt.NativeObject.Dispose();
            }
        }
    }
}
