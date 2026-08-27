using Avalonia.Controls;
using Avalonia.Platform.Storage;
using InterviewFlow.Core.Config;
using InterviewFlow.Core.Models;
using InterviewFlow.Core.ResumePipeline;

namespace InterviewFlow.App.Services;

/// <summary>
/// .docx export flow (docs/06 §6.6): build from the tagged text using the
/// data-folder template when present, then a Save dialog (replacing the
/// original's pywebview save_file_dialog). Returns the written path so callers
/// can show the "📂 Open folder" bar.
/// </summary>
public static class ResumeExportService
{
    public static async Task<string?> ExportAsync(
        Window owner, AppConfig config, InterviewState state, string taggedText)
    {
        var suggested = DocxExporter.BuildExportFilename(config.ResumeName, state.CompanyName);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export resume",
            SuggestedFileName = suggested,
            DefaultExtension = "docx",
            FileTypeChoices = [new FilePickerFileType("Word document") { Patterns = ["*.docx"] }],
        });

        var path = file?.TryGetLocalPath();
        if (path is null)
            return null;

        var bytes = DocxExporter.Build(
            taggedText,
            config.ResumeName,
            config.ResumeContact,
            DocxExporter.FindTemplate(config.DataDir()));
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}
