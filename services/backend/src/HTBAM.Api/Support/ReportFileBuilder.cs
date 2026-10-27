using System.Collections;
using System.Globalization;
using System.Reflection;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HTBAM.Api.Support;

public static class ReportFileBuilder
{
    private const int PdfRowLimit = 300;
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static byte[] Excel(object report, string title)
    {
        using var workbook = new XLWorkbook();
        var overview = workbook.Worksheets.Add("Tong hop");
        overview.Cell(1, 1).Value = title;
        overview.Cell(1, 1).Style.Font.Bold = true;
        overview.Cell(1, 1).Style.Font.FontSize = 16;
        var row = 3;

        foreach (var property in report.GetType().GetProperties())
        {
            var value = property.GetValue(report);
            if (value is null) continue;
            if (IsScalar(value.GetType()))
            {
                if (!IsVisibleField(property.Name)) continue;
                WriteOverviewRow(overview, ref row, Label(property.Name), value, property.Name);
                continue;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                var items = enumerable.Cast<object?>().Where(x => x is not null).Cast<object>().ToList();
                if (property.Name.Equals("Attention", StringComparison.OrdinalIgnoreCase)) continue;
                AddCollectionSheet(workbook, SheetName(property.Name, workbook), items);
                continue;
            }

            overview.Cell(row, 1).Value = Label(property.Name);
            overview.Cell(row, 1).Style.Font.Bold = true;
            row++;
            foreach (var nested in ScalarProperties(value.GetType()))
            {
                var nestedValue = nested.GetValue(value);
                if (nestedValue is not null) WriteOverviewRow(overview, ref row, Label(nested.Name), nestedValue, nested.Name);
            }

            if (property.Name.Equals("Outputs", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var output in value.GetType().GetProperties())
                {
                    var outputValue = output.GetValue(value);
                    if (outputValue is null) continue;
                    var status = outputValue.GetType().GetProperty("Status")?.GetValue(outputValue);
                    var count = outputValue.GetType().GetProperty("Count")?.GetValue(outputValue);
                    var available = string.Equals(Convert.ToString(status, Vi), "AVAILABLE", StringComparison.OrdinalIgnoreCase);
                    WriteOverviewRow(overview, ref row, Label(output.Name), $"{(available ? "Sẵn sàng" : "Chưa có dữ liệu")} · {count} bản ghi");
                }
            }
            row++;
        }

        overview.Columns().AdjustToContents();
        overview.SheetView.FreezeRows(1);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static byte[] Pdf(object report, string title)
    {
        var sections = CollectionSections(report).ToList();
        return Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(9));
                page.Header().Text(title).Bold().FontSize(18).FontColor(Colors.Blue.Darken2);
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Hệ thống giám sát lớp học · ");
                    text.CurrentPageNumber();
                    text.Span("/");
                    text.TotalPages();
                });
                page.Content().PaddingVertical(12).Column(column =>
                {
                    column.Spacing(10);
                    foreach (var property in report.GetType().GetProperties())
                    {
                        var value = property.GetValue(report);
                        if (value is null || value is IEnumerable && value is not string) continue;
                        if (IsScalar(value.GetType()))
                        {
                            if (!IsVisibleField(property.Name)) continue;
                            column.Item().Text($"{Label(property.Name)}: {Format(value, property.Name)}");
                            continue;
                        }

                        if (property.Name.Equals("Outputs", StringComparison.OrdinalIgnoreCase))
                        {
                            column.Item().Text("Trạng thái dữ liệu").Bold().FontSize(12);
                            var outputRows = value.GetType().GetProperties().Select(output =>
                            {
                                var item = output.GetValue(value);
                                return new[]
                                {
                                    Label(output.Name),
                                    Format(item?.GetType().GetProperty("Status")?.GetValue(item)),
                                    Format(item?.GetType().GetProperty("Count")?.GetValue(item), "Count"),
                                    string.Equals(Convert.ToString(item?.GetType().GetProperty("Status")?.GetValue(item), Vi), "AVAILABLE", StringComparison.OrdinalIgnoreCase) ? "Dữ liệu đã sẵn sàng" : "Chưa có dữ liệu"
                                };
                            }).ToList();
                            column.Item().Element(container => DrawTable(container, ["Dữ liệu", "Trạng thái", "Số lượng", "Ghi chú"], outputRows));
                            continue;
                        }

                        var rows = ScalarProperties(value.GetType())
                            .Select(p => new[] { Label(p.Name), Format(p.GetValue(value), p.Name) }).ToList();
                        if (rows.Count == 0) continue;
                        column.Item().Text(Label(property.Name)).Bold().FontSize(12);
                        column.Item().Element(container => DrawTable(container, ["Chỉ số", "Giá trị"], rows));
                    }

                    foreach (var section in sections)
                    {
                        column.Item().PaddingTop(6).Text($"{Label(section.Name)} ({section.Items.Count})").Bold().FontSize(12);
                        if (section.Items.Count == 0)
                        {
                            column.Item().Text("Không có dữ liệu.").Italic().FontColor(Colors.Grey.Darken1);
                            continue;
                        }
                        var properties = ScalarProperties(section.Items[0].GetType()).Take(12).ToArray();
                        var rows = section.Items.Take(PdfRowLimit)
                            .Select(item => properties.Select(p => Format(p.GetValue(item), p.Name)).ToArray()).ToList();
                        column.Item().Element(container => DrawTable(container, properties.Select(p => Label(p.Name)).ToArray(), rows));
                        if (section.Items.Count > PdfRowLimit)
                            column.Item().Text($"PDF hiển thị {PdfRowLimit}/{section.Items.Count} dòng; file Excel chứa toàn bộ dữ liệu.").Italic();
                    }
                });
            });
        }).GeneratePdf();
    }

    private static IEnumerable<(string Name, List<object> Items)> CollectionSections(object report)
    {
        foreach (var property in report.GetType().GetProperties())
        {
            if (property.Name.Equals("Attention", StringComparison.OrdinalIgnoreCase)) continue;
            if (property.GetValue(report) is not IEnumerable enumerable || enumerable is string) continue;
            yield return (property.Name, enumerable.Cast<object?>().Where(x => x is not null).Cast<object>().ToList());
        }
    }

    private static void DrawTable(IContainer container, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        if (headers.Count == 0) return;
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                for (var index = 0; index < headers.Count; index++) columns.RelativeColumn();
            });
            table.Header(header =>
            {
                foreach (var value in headers)
                    header.Cell().Background(Colors.Blue.Lighten4).Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4).Text(value).SemiBold();
            });
            foreach (var row in rows)
                foreach (var value in row)
                    table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(value);
        });
    }

    private static void AddCollectionSheet(XLWorkbook workbook, string name, IReadOnlyList<object> items)
    {
        var sheet = workbook.Worksheets.Add(name);
        if (items.Count == 0)
        {
            sheet.Cell(1, 1).Value = "Không có dữ liệu";
            return;
        }
        var properties = ScalarProperties(items[0].GetType()).ToArray();
        for (var index = 0; index < properties.Length; index++)
        {
            sheet.Cell(1, index + 1).Value = Label(properties[index].Name);
            sheet.Cell(1, index + 1).Style.Font.Bold = true;
            sheet.Cell(1, index + 1).Style.Fill.BackgroundColor = XLColor.LightBlue;
        }
        for (var row = 0; row < items.Count; row++)
            for (var column = 0; column < properties.Length; column++)
                SetCell(sheet.Cell(row + 2, column + 1), properties[column].GetValue(items[row]), properties[column].Name);
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
        if (properties.Length > 0) sheet.Range(1, 1, items.Count + 1, properties.Length).SetAutoFilter();
    }

    private static void WriteOverviewRow(IXLWorksheet sheet, ref int row, string label, object value, string fieldName = "")
    {
        sheet.Cell(row, 1).Value = label;
        SetCell(sheet.Cell(row, 2), value, fieldName);
        row++;
    }

    private static void SetCell(IXLCell cell, object? value, string fieldName = "")
    {
        if (value is decimal or double or float && IsPercentField(fieldName))
        {
            var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            cell.Value = fieldName.EndsWith("ScorePercent", StringComparison.OrdinalIgnoreCase) ? number / 100m : number;
            cell.Style.NumberFormat.Format = "0.0%";
            return;
        }
        switch (value)
        {
            case null: cell.Value = ""; break;
            case DateTime date: cell.Value = date; cell.Style.DateFormat.Format = "dd/MM/yyyy HH:mm:ss"; break;
            case DateOnly date: cell.Value = date.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "dd/MM/yyyy"; break;
            case bool flag: cell.Value = flag ? "Có" : "Không"; break;
            case int number: cell.Value = number; break;
            case long number: cell.Value = number; break;
            case double number: cell.Value = number; break;
            case float number: cell.Value = number; break;
            case decimal number: cell.Value = number; break;
            default: cell.Value = Format(value, fieldName); break;
        }
    }

    private static IEnumerable<PropertyInfo> ScalarProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && IsScalar(p.PropertyType) && IsVisibleField(p.Name));

    private static bool IsVisibleField(string name)
    {
        var key = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return key != "id" && !key.EndsWith("id", StringComparison.Ordinal) &&
               !key.Contains("provider", StringComparison.Ordinal) && !key.Contains("model", StringComparison.Ordinal) &&
               !key.Contains("confidence", StringComparison.Ordinal) && !key.Contains("embedding", StringComparison.Ordinal) &&
               !key.Contains("track", StringComparison.Ordinal) && !key.Contains("probability", StringComparison.Ordinal) &&
               !key.Contains("quality", StringComparison.Ordinal) && !key.Contains("margin", StringComparison.Ordinal) &&
               !key.Contains("endpoint", StringComparison.Ordinal) && !key.Contains("errorcode", StringComparison.Ordinal) &&
               key is not "metadatajson" and not "evidenceref" and not "externaleventid";
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
               type == typeof(DateTime) || type == typeof(DateOnly) || type == typeof(TimeOnly) ||
               type == typeof(Guid);
    }

    private static string Format(object? value, string fieldName = "")
    {
        if (value is decimal or double or float && IsPercentField(fieldName))
        {
            var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (!fieldName.EndsWith("ScorePercent", StringComparison.OrdinalIgnoreCase)) number *= 100m;
            return number.ToString("0.#", Vi) + "%";
        }
        return value switch
    {
        null => "—",
        DateTime date => date.ToString("dd/MM/yyyy HH:mm:ss", Vi),
        DateOnly date => date.ToString("dd/MM/yyyy", Vi),
        decimal number => number.ToString("0.####", Vi),
        double number => number.ToString("0.####", Vi),
        bool flag => flag ? "Có" : "Không",
        _ => TranslateValue(Convert.ToString(value, Vi) ?? "—")
    };
    }

    private static string TranslateValue(string value) => value.ToUpperInvariant() switch
    {
        "AVAILABLE" => "Sẵn sàng",
        "EMPTY" or "NOT_CREATED" => "Chưa có dữ liệu",
        "OPEN" => "Đang mở",
        "ACK" => "Đã xác nhận",
        "CLOSED" => "Đã đóng",
        "PRESENT" => "Có mặt",
        "PARTIAL" => "Có mặt một phần",
        "ABSENT" => "Vắng mặt",
        "FOCUSED" => "Tập trung",
        "DISTRACTED" => "Mất tập trung",
        "SLEEPY" => "Buồn ngủ",
        "ACTIVE" => "Hoạt động",
        "UNKNOWN" => "Chưa xác định",
        _ => value
    };

    private static bool IsPercentField(string name)
    {
        var key = name.ToLowerInvariant();
        return key.EndsWith("scorepercent") || key.Contains("ratio") || key.Contains("probability") ||
               key.Contains("quality") || key.Contains("confidence") || key.Contains("margin") ||
               key is "focused" or "distracted" or "sleepy" or "active" or
               "averagefocused" or "averagedistracted" or "averagesleepy" or "averageactive";
    }

    private static string SheetName(string source, XLWorkbook workbook)
    {
        var baseName = new string(Label(source).Where(c => c is not ':' and not '\\' and not '/' and not '?' and not '*' and not '[' and not ']').ToArray());
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Du lieu";
        baseName = baseName[..Math.Min(31, baseName.Length)];
        var candidate = baseName;
        var suffix = 2;
        while (workbook.Worksheets.Any(x => x.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            var tail = $" {suffix++}";
            candidate = baseName[..Math.Min(31 - tail.Length, baseName.Length)] + tail;
        }
        return candidate;
    }

    private static string Label(string name) => name.ToLowerInvariant() switch
    {
        "session" => "Buổi học",
        "student" => "Sinh viên",
        "classsection" => "Lớp học phần",
        "summary" => "Tổng hợp",
        "outputs" => "Trạng thái dữ liệu",
        "students" => "Sinh viên",
        "sessions" => "Các buổi học",
        "timeline" => "Sự kiện hành vi",
        "behaviorevents" => "Sự kiện hành vi",
        "attendance" => "Chuyên cần",
        "alerts" => "Cảnh báo",
        "identities" => "Kết quả nhận diện",
        "identitylinks" => "Chi tiết nhận diện",
        "tracksegments" => "Khoảng thời gian được ghi nhận",
        "videos" or "video" => "Video",
        "summaries" => "Báo cáo tổng hợp",
        "averageattendancescorepercent" or "scorepercent" => "Điểm chuyên cần (%)",
        "policyscore" => "Điểm theo quy định",
        "presentseconds" => "Thời gian hiện diện (giây)",
        "observedseconds" or "totalobservedseconds" => "Thời gian quan sát (giây)",
        "alertdurationseconds" => "Thời lượng cảnh báo (giây)",
        _ => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()))
    };
}
