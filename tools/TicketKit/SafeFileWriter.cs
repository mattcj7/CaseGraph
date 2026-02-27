using System.Text;

namespace TicketKit;

public enum SafeWriteOutcome
{
    Created,
    Overwritten,
    Skipped
}

public static class SafeFileWriter
{
    public static async Task<SafeWriteOutcome> WriteTemplateAsync(
        string templatePath,
        string destinationPath,
        IReadOnlyDictionary<string, string> replacements,
        bool overwrite,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("Template file not found.", templatePath);
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new InvalidOperationException(
                $"Unable to resolve destination directory for '{destinationPath}'."
            );
        }

        Directory.CreateDirectory(destinationDirectory);

        var destinationExists = File.Exists(destinationPath);
        if (destinationExists && !overwrite)
        {
            return SafeWriteOutcome.Skipped;
        }

        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        try
        {
            await using var destinationStream = new FileStream(
                destinationPath,
                mode,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true
            );
            await using var writer = new StreamWriter(
                destinationStream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            );
            await WriteRenderedTemplateAsync(templatePath, writer, replacements, cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        catch (IOException) when (!overwrite && File.Exists(destinationPath))
        {
            return SafeWriteOutcome.Skipped;
        }

        return destinationExists ? SafeWriteOutcome.Overwritten : SafeWriteOutcome.Created;
    }

    private static async Task WriteRenderedTemplateAsync(
        string templatePath,
        StreamWriter writer,
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken cancellationToken
    )
    {
        await using var templateStream = new FileStream(
            templatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true
        );
        using var reader = new StreamReader(templateStream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            var renderedLine = TemplateRenderer.RenderTokens(line, replacements);
            await writer.WriteLineAsync(renderedLine.AsMemory(), cancellationToken);
        }
    }
}
