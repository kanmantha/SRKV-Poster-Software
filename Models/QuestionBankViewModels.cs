namespace DailyPosterGenerator.Models;

public class UploadPdfViewModel
{
    public Microsoft.AspNetCore.Http.IFormFile? PdfFile { get; set; }
    public string? ExtractedText { get; set; }
    public string? FileName { get; set; }
    public int PageCount { get; set; }
    public int CharCount { get; set; }
}

public class GenerateQuestionsViewModel
{
    public string ExtractedText { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int QuestionCount { get; set; } = 25;
    public string Difficulty { get; set; } = "mixed";
    public string QuestionType { get; set; } = "all";
    public string? AdditionalInstructions { get; set; }
}

public class GeneratedQuestionsViewModel
{
    public string Topic { get; set; } = string.Empty;
    public int QuestionCount { get; set; }
    public List<GeneratedQuestion> Questions { get; set; } = new();
    public string RawResponse { get; set; } = string.Empty;
    public string? FileName { get; set; }
}

public class GeneratedQuestion
{
    public int Number { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? OptionA { get; set; }
    public string? OptionB { get; set; }
    public string? OptionC { get; set; }
    public string? OptionD { get; set; }
    public string? Answer { get; set; }
    public string Type { get; set; } = "short_answer";
    public string Difficulty { get; set; } = "medium";
}
