using System.Globalization;
using System.Text;
using HTBAM.Application.DTOs;

namespace HTBAM.Application.Services;

public static class LlmPromptBuilder
{
    public const string SystemInstruction = """
        Bạn là trợ lý tạo nhận xét cho hệ thống phân tích hành vi học tập HTBAM.
        Chỉ sử dụng dữ liệu tổng hợp được cung cấp. Không tự tạo dữ liệu, không suy đoán nguyên nhân,
        không chẩn đoán tâm lý hoặc y tế, không phán xét người học và không thay đổi dữ liệu HTBAM.
        Viết bằng tiếng Việt, từ 2 đến 4 câu, khách quan và dễ đọc. Không dùng các từ ngữ quy chụp
        như "lười", "kém" hoặc "có vấn đề". Không suy ra điểm nếu điểm không được cung cấp.
        Nếu chất lượng quan sát thấp, phải nêu rõ giới hạn độ tin cậy.
        Có thể đề xuất việc tiếp tục theo dõi hoặc hỗ trợ theo cách trung tính.
        """;

    public static string Build(LlmCommentRequest request)
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder()
            .AppendLine("Hãy tạo nhận xét từ đúng dữ liệu tổng hợp sau:")
            .AppendLine($"SubjectType: {Sanitize(request.SubjectType)}")
            .AppendLine($"SubjectId: {Sanitize(request.SubjectId)}")
            .AppendLine($"SubjectName: {Sanitize(request.SubjectName)}")
            .AppendLine($"Focused: {(request.FocusedRatio * 100).ToString("0.##", c)}%")
            .AppendLine($"Distracted: {(request.DistractedRatio * 100).ToString("0.##", c)}%")
            .AppendLine($"Sleepy: {(request.SleepyRatio * 100).ToString("0.##", c)}%")
            .AppendLine($"Active: {(request.ActiveRatio * 100).ToString("0.##", c)}%")
            .AppendLine($"Alerts: {request.AlertCount}")
            .AppendLine($"ObservationQuality: {(request.ObservationQuality * 100).ToString("0.##", c)}%");

        if (request.AttendanceScore is not null)
            text.AppendLine($"AttendanceScorePercent: {request.AttendanceScore.Value.ToString("0.##", c)}%");
        if (request.PresentSeconds is not null)
            text.AppendLine($"PresentSeconds: {request.PresentSeconds.Value}");
        if (request.ObservedSeconds is not null)
            text.AppendLine($"ObservedSeconds: {request.ObservedSeconds.Value}");
        if (!string.IsNullOrWhiteSpace(request.AdditionalContext))
            text.AppendLine($"AdditionalContext: {Sanitize(request.AdditionalContext)}");

        return text.AppendLine("Không thêm số liệu ngoài danh sách trên.").ToString();
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

