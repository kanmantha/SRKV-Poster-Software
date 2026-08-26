using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DailyPosterGenerator.Models;
using UglyToad.PdfPig;

namespace DailyPosterGenerator.Services;

public interface IQuestionBankService
{
    Task<PdfExtractionResult> ExtractTextFromPdfAsync(Stream pdfStream, string fileName);
    Task<GeneratedQuestionsViewModel> GenerateQuestionsAsync(GenerateQuestionsViewModel request, CancellationToken ct = default);
}

public class PdfExtractionResult
{
    public string Text { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public int CharCount { get; set; }
    public string FileName { get; set; } = string.Empty;
}

public class QuestionBankService : IQuestionBankService
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ILogger<QuestionBankService> _logger;

    public QuestionBankService(HttpClient http, ISettingsService settings, ILogger<QuestionBankService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public Task<PdfExtractionResult> ExtractTextFromPdfAsync(Stream pdfStream, string fileName)
    {
        var sb = new StringBuilder();
        int pageCount = 0;

        using (var document = PdfDocument.Open(pdfStream))
        {
            pageCount = document.NumberOfPages;
            foreach (var page in document.GetPages())
            {
                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                    sb.AppendLine();
                }
            }
        }

        var extracted = sb.ToString().Trim();
        return Task.FromResult(new PdfExtractionResult
        {
            Text = extracted,
            PageCount = pageCount,
            CharCount = extracted.Length,
            FileName = fileName
        });
    }

    public async Task<GeneratedQuestionsViewModel> GenerateQuestionsAsync(GenerateQuestionsViewModel request, CancellationToken ct = default)
    {
        var endpoint = (await _settings.GetAsync("ai.endpoint", "https://api.openai.com/v1"))!.TrimEnd('/');
        var apiKey = await _settings.GetAsync("ai.apiKey");
        var model = await _settings.GetAsync("ai.chatModel", "gpt-4o-mini");
        var timeoutSeconds = int.Parse(await _settings.GetAsync("ai.timeoutSeconds", "90") ?? "90");
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(timeoutSeconds, 180));

        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(request);

        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.8,
            max_tokens = 8000
        };

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _http.SendAsync(requestMessage, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("AI question generation API returned {Status}: {Body}", response.StatusCode, body.Length > 300 ? body[..300] : body);
            throw new InvalidOperationException($"AI service failed ({response.StatusCode}). Make sure AI is configured in Settings.");
        }

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        var questions = ParseQuestions(content ?? string.Empty, request.QuestionCount);

        return new GeneratedQuestionsViewModel
        {
            Topic = ExtractTopic(request.ExtractedText),
            QuestionCount = questions.Count,
            Questions = questions,
            RawResponse = content ?? string.Empty,
            FileName = request.FileName
        };
    }

    private static string BuildSystemPrompt()
    {
        return @"You are an expert question paper generator, similar to a skilled teacher or exam setter. Your task is to generate high-quality, diverse, and meaningful questions based on the provided source material.

CRITICAL RULES:
1. Generate EXACTLY the number of questions requested.
2. Every question MUST be unique — no duplicates, no rephrased duplicates, no near-duplicates.
3. Cover ALL topics and subtopics found in the source material evenly.
4. Vary difficulty levels: include easy (30%), medium (50%), and hard (20%) questions unless told otherwise.
5. Use diverse question formats: definitions, applications, problem-solving, conceptual, analytical, comparison, true/false, MCQ, short answer, and long answer.
6. Questions should test understanding, not just recall. Include questions that require thinking, applying concepts, and solving problems.
7. For MCQ questions, provide 4 options (A, B, C, D) with exactly one correct answer.
8. For non-MCQ questions, provide a model answer after each question.
9. Number all questions sequentially starting from 1.
10. Do NOT repeat the same concept in multiple questions — each question should test a different concept or angle.
11. Ensure questions are factually accurate and well-formed.
12. Match the academic level of the source material.

OUTPUT FORMAT (JSON):
{
  ""questions"": [
    {
      ""number"": 1,
      ""text"": ""Question text here"",
      ""type"": ""mcq"",
      ""difficulty"": ""easy"",
      ""options"": {""a"": ""Option A"", ""b"": ""Option B"", ""c"": ""Option C"", ""d"": ""Option D""},
      ""answer"": ""a"",
      ""explanation"": ""Brief explanation of the answer""
    },
    {
      ""number"": 2,
      ""text"": ""Question text here"",
      ""type"": ""short_answer"",
      ""difficulty"": ""medium"",
      ""options"": null,
      ""answer"": ""Model answer here"",
      ""explanation"": """"
    }
  ]
}

Valid types: mcq, true_false, short_answer, long_answer, problem_solving, definition, comparison
Valid difficulties: easy, medium, hard

Always respond with valid JSON only. No markdown, no extra text outside the JSON.";
    }

    private static string BuildUserPrompt(GenerateQuestionsViewModel request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SOURCE MATERIAL ===");
        sb.AppendLine(request.ExtractedText);
        sb.AppendLine("=== END SOURCE MATERIAL ===");
        sb.AppendLine();

        sb.AppendLine($"Generate exactly {request.QuestionCount} questions based on the above source material.");
        sb.AppendLine();

        if (request.Difficulty != "mixed")
        {
            sb.AppendLine($"Difficulty level: {request.Difficulty}");
        }
        else
        {
            sb.AppendLine("Difficulty: Mixed (30% easy, 50% medium, 20% hard)");
        }

        if (request.QuestionType != "all")
        {
            sb.AppendLine($"Question type: {request.QuestionType}");
        }
        else
        {
            sb.AppendLine("Question types: Mix of MCQ, short answer, long answer, true/false, problem-solving, and definitions.");
        }

        if (!string.IsNullOrWhiteSpace(request.AdditionalInstructions))
        {
            sb.AppendLine();
            sb.AppendLine($"Additional instructions: {request.AdditionalInstructions}");
        }

        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Every question must be unique and test a DIFFERENT concept. Do not repeat the same idea in different words. Cover the entire syllabus evenly.");

        return sb.ToString();
    }

    private static List<GeneratedQuestion> ParseQuestions(string raw, int expectedCount)
    {
        var questions = new List<GeneratedQuestion>();

        try
        {
            // Try to find JSON in the response
            var jsonStart = raw.IndexOf('{');
            var jsonEnd = raw.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = raw[jsonStart..(jsonEnd + 1)];
                using var doc = JsonDocument.Parse(jsonStr);

                if (doc.RootElement.TryGetProperty("questions", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var q in arr.EnumerateArray())
                    {
                        var question = new GeneratedQuestion
                        {
                            Number = q.TryGetProperty("number", out var num) ? num.GetInt32() : questions.Count + 1,
                            Text = q.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "",
                            Type = q.TryGetProperty("type", out var tp) ? tp.GetString() ?? "short_answer" : "short_answer",
                            Difficulty = q.TryGetProperty("difficulty", out var diff) ? diff.GetString() ?? "medium" : "medium",
                            Answer = q.TryGetProperty("answer", out var ans) ? ans.GetString() : null
                        };

                        if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
                        {
                            question.OptionA = opts.TryGetProperty("a", out var a) ? a.GetString() : null;
                            question.OptionB = opts.TryGetProperty("b", out var b) ? b.GetString() : null;
                            question.OptionC = opts.TryGetProperty("c", out var c) ? c.GetString() : null;
                            question.OptionD = opts.TryGetProperty("d", out var d) ? d.GetString() : null;
                        }

                        if (!string.IsNullOrWhiteSpace(question.Text))
                        {
                            questions.Add(question);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to text parsing
        }

        // Fallback: if JSON parsing failed, try to extract questions from plain text
        if (questions.Count == 0)
        {
            questions = ParseQuestionsFromText(raw);
        }

        return questions.Take(expectedCount).ToList();
    }

    private static List<GeneratedQuestion> ParseQuestionsFromText(string raw)
    {
        var questions = new List<GeneratedQuestion>();
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var currentText = new StringBuilder();
        int currentNumber = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Match patterns like "1.", "Q1)", "1)", "Question 1:", etc.
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(?:Q(?:uestion)?\s*)?(\d+)[.\)\:]");
            if (match.Success)
            {
                if (currentNumber > 0 && currentText.Length > 0)
                {
                    questions.Add(new GeneratedQuestion
                    {
                        Number = currentNumber,
                        Text = currentText.ToString().Trim(),
                        Type = "short_answer",
                        Difficulty = "medium"
                    });
                }
                currentNumber = int.Parse(match.Groups[1].Value);
                currentText.Clear();
                currentText.Append(System.Text.RegularExpressions.Regex.Replace(trimmed, @"^(?:Q(?:uestion)?\s*)?\d+[.\)\:]\s*", ""));
            }
            else if (currentNumber > 0)
            {
                currentText.Append(" ").Append(trimmed);
            }
        }

        if (currentNumber > 0 && currentText.Length > 0)
        {
            questions.Add(new GeneratedQuestion
            {
                Number = currentNumber,
                Text = currentText.ToString().Trim(),
                Type = "short_answer",
                Difficulty = "medium"
            });
        }

        return questions;
    }

    private static string ExtractTopic(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "General";

        // Take first 200 chars, try to find a topic indicator
        var head = text.Length > 500 ? text[..500] : text;
        var lines = head.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Look for title-like first line
        foreach (var line in lines.Take(5))
        {
            var trimmed = line.Trim();
            if (trimmed.Length is > 3 and <= 100 && !trimmed.EndsWith('?') && !trimmed.EndsWith('.'))
            {
                return trimmed;
            }
        }

        return head.Split(' ').Take(6).Aggregate((a, b) => $"{a} {b}");
    }
}
