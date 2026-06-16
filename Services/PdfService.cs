using BankAdmin.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace BankAdmin.Services;

/// <summary>
/// Generates the client + accounts certificate as a PDF, entirely server-side
/// (the BankOS API is never asked to render documents). Uses QuestPDF.
/// </summary>
public class PdfService
{
    private readonly byte[]? _logo;

    private const string Navy = "#0c1f6e";
    private const string Purple = "#7c12fd";
    private const string Blue = "#0463fd";
    private const string Cyan = "#00a8e8";
    private const string Green = "#22c55e";
    private const string Ink = "#0f172a";
    private const string Slate = "#475569";
    private const string Muted = "#94a3b8";
    private const string Line = "#e8edf6";
    private const string Soft = "#f6f8fc";

    public PdfService(IWebHostEnvironment env)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var logoPath = Path.Combine(env.WebRootPath ?? "wwwroot", "img", "logo.png");
        if (File.Exists(logoPath)) _logo = File.ReadAllBytes(logoPath);
    }

    public byte[] GenerateClientCertificate(string tenantName, UserModel client, List<AccountModel> accounts, string? issuedBy = null)
    {
        var reference = $"BANKOS-{tenantName.ToUpperInvariant()}-{client.Id[..Math.Min(8, client.Id.Length)].ToUpperInvariant()}-{DateTime.Now:yyyyMMdd-HHmm}";

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.DefaultTextStyle(x => x.FontFamily("Helvetica").FontColor(Ink).LineHeight(1.25f));

                page.Content().Column(col =>
                {
                    col.Item().Element(c => Header(c, tenantName));
                    col.Item().PaddingHorizontal(48).PaddingTop(28).Element(c => Body(c, tenantName, client, accounts, issuedBy, reference));
                    col.Item().Extend();
                    col.Item().Element(Footer);
                });
            });
        });

        return doc.GeneratePdf();
    }

    private void Header(IContainer c, string tenantName)
    {
        c.Column(h =>
        {
            h.Item().Background(Navy).PaddingHorizontal(48).PaddingTop(40).PaddingBottom(30).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Row(brand =>
                    {
                        if (_logo != null)
                            brand.ConstantItem(54).Height(54).AlignMiddle().Image(_logo).FitArea();
                        brand.ConstantItem(14);
                        brand.AutoItem().AlignMiddle().Column(w =>
                        {
                            w.Item().Text(txt =>
                            {
                                txt.Span("Bank").FontColor("#ffffff").FontSize(30).Bold();
                                txt.Span("O").FontColor("#c084fc").FontSize(30).Bold();
                                txt.Span("s").FontColor("#4ade80").FontSize(30).Bold();
                            });
                            w.Item().Text("Tu banco, en una sola plataforma.").FontColor("#9bb4ff").FontSize(10);
                        });
                    });

                    left.Item().PaddingTop(26).Text("CERTIFICADO BANCARIO")
                        .FontColor("#eaf1ff").FontSize(19).Bold().LetterSpacing(0.04f);
                    left.Item().PaddingTop(3).Text($"Emitido por {tenantName} · Plataforma BankOs")
                        .FontColor("#9bb4ff").FontSize(11);
                });

                row.ConstantItem(120).AlignTop().Column(seal =>
                {
                    seal.Item().Border(1.5f).BorderColor("#3b56c9")
                        .Background("#101f5e").Padding(12).Column(s =>
                    {
                        s.Item().AlignCenter().Text("✓").FontColor("#4ade80").FontSize(30).Bold();
                        s.Item().AlignCenter().PaddingTop(2).Text("VERIFICADO").FontColor("#ffffff").FontSize(8).Bold().LetterSpacing(0.1f);
                        s.Item().AlignCenter().Text("BankOs").FontColor("#9bb4ff").FontSize(7).LetterSpacing(0.12f);
                    });
                });
            });

            h.Item().Element(SpectrumRail);
        });
    }

    private void SpectrumRail(IContainer c)
    {
        var stops = new (float Pos, (int R, int G, int B) Rgb)[]
        {
            (0.00f, (0x0c, 0x1f, 0x6e)),
            (0.22f, (0x3a, 0x1d, 0x9e)),
            (0.42f, (0x7c, 0x12, 0xfd)),
            (0.62f, (0x04, 0x63, 0xfd)),
            (0.80f, (0x00, 0xa8, 0xe8)),
            (1.00f, (0x22, 0xc5, 0x5e)),
        };
        const int segments = 60;
        c.Height(6).Row(row =>
        {
            for (int i = 0; i < segments; i++)
            {
                var pos = i / (float)(segments - 1);
                row.RelativeItem().Background(Interpolate(stops, pos));
            }
        });
    }

    private static string Interpolate((float Pos, (int R, int G, int B) Rgb)[] stops, float pos)
    {
        for (int i = 1; i < stops.Length; i++)
        {
            if (pos <= stops[i].Pos)
            {
                var (p0, c0) = stops[i - 1];
                var (p1, c1) = stops[i];
                var t = (pos - p0) / Math.Max(0.0001f, p1 - p0);
                int r = (int)(c0.R + (c1.R - c0.R) * t);
                int g = (int)(c0.G + (c1.G - c0.G) * t);
                int b = (int)(c0.B + (c1.B - c0.B) * t);
                return $"#{r:x2}{g:x2}{b:x2}";
            }
        }
        var last = stops[^1].Rgb;
        return $"#{last.R:x2}{last.G:x2}{last.B:x2}";
    }

    private void Body(IContainer c, string tenantName, UserModel client, List<AccountModel> accounts, string? issuedBy, string reference)
    {
        var active = accounts.Where(a => a.IsActive).ToList();

        c.Column(b =>
        {
            b.Item().Border(1).BorderColor(Line).Background(Soft).Padding(20).Text(txt =>
            {
                txt.DefaultTextStyle(s => s.FontSize(12.5f).FontColor(Slate).LineHeight(1.5f));
                txt.Span("BankOs certifica que ");
                txt.Span(client.Name).Bold().FontColor(Navy);
                txt.Span(", identificado(a) con el correo ");
                txt.Span(client.Email).Bold().FontColor(Purple);
                txt.Span($", es {(client.IsAdmin ? "administrador(a)" : "cliente")} registrado(a) en ");
                txt.Span(tenantName).Bold().FontColor(Navy);
                txt.Span(", con las cuentas y saldos que se detallan a continuación.");
            });

            Section(b, "Identidad del titular");
            b.Item().PaddingTop(14).Row(row =>
            {
                row.RelativeItem().Column(l =>
                {
                    Field(l, "Nombre", client.Name);
                    Field(l, "Correo electrónico", client.Email);
                });
                row.ConstantItem(28);
                row.RelativeItem().Column(r =>
                {
                    Field(r, "Identificador", client.Id, mono: true);
                    Field(r, "Estado", client.IsActive ? "Activo" : "Inactivo", color: client.IsActive ? Green : "#dc2626");
                });
            });

            Section(b, $"Cuentas bancarias ({accounts.Count})");
            if (accounts.Count > 0)
            {
                b.Item().PaddingTop(12).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(3);
                        cols.RelativeColumn(1.4f);
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(1.5f);
                    });
                    table.Header(hd =>
                    {
                        foreach (var h in new[] { "Número de cuenta", "Moneda", "Saldo", "Estado" })
                            hd.Cell().Background(Soft).Padding(8).Text(h).FontSize(10).Bold().FontColor(Slate);
                    });
                    foreach (var a in accounts)
                    {
                        table.Cell().BorderBottom(1).BorderColor(Line).Padding(8).Text(a.AccountNumber).FontSize(11).FontFamily("Courier");
                        table.Cell().BorderBottom(1).BorderColor(Line).Padding(8).Text(a.Currency).FontSize(11);
                        table.Cell().BorderBottom(1).BorderColor(Line).Padding(8).Text($"{a.Balance:N2}").FontSize(11);
                        table.Cell().BorderBottom(1).BorderColor(Line).Padding(8)
                            .Text(a.IsActive ? "Activa" : a.Status).FontSize(11).FontColor(a.IsActive ? Green : Muted);
                    }
                });
            }
            else
            {
                b.Item().PaddingTop(12).Text("El titular no posee cuentas registradas.").FontSize(11).FontColor(Muted);
            }

            // Balance summary by currency (active accounts)
            if (active.Count > 0)
            {
                Section(b, "Resumen de saldos activos");
                var byCurrency = active.GroupBy(a => a.Currency)
                    .Select(g => (Currency: g.Key, Total: g.Sum(x => x.Balance))).ToList();
                b.Item().PaddingTop(12).Row(row =>
                {
                    foreach (var (cur, total) in byCurrency.Take(4))
                    {
                        Metric(row, cur, $"{total:N2}", Blue);
                        row.ConstantItem(12);
                    }
                });
            }

            Section(b, "Emisión");
            b.Item().PaddingTop(14).Row(row =>
            {
                row.RelativeItem().Column(l =>
                {
                    Field(l, "Fecha de emisión", DateTime.Now.ToString("dd 'de' MMMM 'de' yyyy, HH:mm"));
                    Field(l, "Banco emisor", tenantName);
                });
                row.ConstantItem(28);
                row.RelativeItem().Column(r =>
                {
                    Field(r, "Emitido por", string.IsNullOrWhiteSpace(issuedBy) ? "Portal BankOs" : issuedBy!);
                    Field(r, "Validez", "Refleja el estado al momento de la emisión");
                });
            });

            b.Item().PaddingTop(24).Border(1).BorderColor(Line).Background(Soft).Padding(16).Row(row =>
            {
                row.RelativeItem().Column(s =>
                {
                    s.Item().Text("SELLO DIGITAL").FontSize(9).Bold().FontColor(Slate).LetterSpacing(0.06f);
                    s.Item().PaddingTop(4).Text(reference).FontFamily("Courier").FontSize(10).FontColor(Purple);
                    s.Item().PaddingTop(4).Text("Documento generado electrónicamente por BankOs. Su autenticidad puede verificarse con la referencia anterior.")
                        .FontSize(8.5f).FontColor(Muted).LineHeight(1.4f);
                });
            });
        });
    }

    private void Footer(IContainer c) =>
        c.Background("#0a1230").PaddingVertical(16).PaddingHorizontal(48).Row(row =>
        {
            row.RelativeItem().AlignMiddle().Text($"BankOs © {DateTime.Now.Year} · Banca digital multi-tenant")
                .FontColor("#8ea2d8").FontSize(9);
            row.RelativeItem().AlignMiddle().AlignRight().Text("Documento confidencial")
                .FontColor("#56689f").FontSize(9);
        });

    private void Section(ColumnDescriptor col, string title)
    {
        col.Item().PaddingTop(24).Text(title.ToUpperInvariant())
            .FontSize(10).Bold().FontColor(Navy).LetterSpacing(0.06f);
        col.Item().PaddingTop(6).Height(1).Background(Line);
    }

    private static void Field(ColumnDescriptor col, string label, string value, bool mono = false, string? color = null)
    {
        col.Item().PaddingBottom(13).Column(item =>
        {
            item.Item().Text(label).FontSize(8.5f).Bold().FontColor(Muted).LetterSpacing(0.03f);
            var t = item.Item().PaddingTop(2).Text(value).FontSize(12).FontColor(color ?? Ink);
            if (mono) t.FontFamily("Courier");
        });
    }

    private static void Metric(RowDescriptor row, string label, string value, string color)
    {
        row.RelativeItem().Border(1).BorderColor(Line).Padding(14).Column(m =>
        {
            m.Item().Text(value).FontSize(18).Bold().FontColor(color);
            m.Item().PaddingTop(2).Text(label).FontSize(9).FontColor(Muted);
        });
    }
}
