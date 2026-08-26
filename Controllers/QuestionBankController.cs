using DailyPosterGenerator.Models;
using DailyPosterGenerator.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DailyPosterGenerator.Controllers;

[Authorize]
public class QuestionBankController : Controller
{
    private readonly IQuestionBankService _questionBank;
    private readonly ILogger<QuestionBankController> _logger;

    public QuestionBankController(IQuestionBankService questionBank, ILogger<QuestionBankController> logger)
    {
        _questionBank = questionBank;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View(new UploadPdfViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(UploadPdfViewModel model, CancellationToken ct)
    {
        if (model.PdfFile is null || model.PdfFile.Length == 0)
        {
            TempData["Error"] = "Please select a PDF file to upload.";
            return RedirectToAction(nameof(Index));
        }

        if (!model.PdfFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Only PDF files are supported.";
            return RedirectToAction(nameof(Index));
        }

        if (model.PdfFile.Length > 20 * 1024 * 1024)
        {
            TempData["Error"] = "File size must be under 20 MB.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            using var stream = model.PdfFile.OpenReadStream();
            var result = await _questionBank.ExtractTextFromPdfAsync(stream, model.PdfFile.FileName);

            var viewModel = new GenerateQuestionsViewModel
            {
                ExtractedText = result.Text,
                FileName = result.FileName,
                QuestionCount = 25
            };

            ViewBag.ExtractedPages = result.PageCount;
            ViewBag.ExtractedChars = result.CharCount;
            ViewBag.ExtractedPreview = result.Text.Length > 2000 ? result.Text[..2000] + "..." : result.Text;

            return View("Generate", viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from PDF {FileName}", model.PdfFile.FileName);
            TempData["Error"] = $"Failed to read PDF: {ex.Message}";
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(GenerateQuestionsViewModel model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model.ExtractedText))
        {
            TempData["Error"] = "No source text available. Please upload a PDF first.";
            return RedirectToAction(nameof(Index));
        }

        if (model.QuestionCount < 1 || model.QuestionCount > 200)
        {
            model.QuestionCount = 25;
        }

        try
        {
            var result = await _questionBank.GenerateQuestionsAsync(model, ct);
            return View("Results", result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Question generation failed: {Error}", ex.Message);
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Question generation failed");
            TempData["Error"] = "Something went wrong while generating questions. Please try again.";
            return RedirectToAction(nameof(Index));
        }
    }
}
