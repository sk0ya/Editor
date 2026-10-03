using System.Text.Json;
using Editor.Controls.Lsp;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

/// <summary>
/// <c>workspace/willRenameFiles</c>／<c>workspace/didRenameFiles</c> と、ホストが fileOperations の
/// filters を読むための <see cref="LspClient.ServerCapabilities"/> を、実プロセスの往復で確かめる
/// （<see cref="LspProtocolTests"/> と同じ PowerShell 製の疑似サーバー）。
/// </summary>
public sealed class LspFileRenameTests
{
    [Fact]
    public void Rename_params_carry_old_and_new_uris()
    {
        var json = JsonSerializer.Serialize(LspClient.CreateFileRenameParams(
            [("file:///c:/w/a.ts", "file:///c:/w/b.ts")]));

        Assert.Equal("""{"files":[{"oldUri":"file:///c:/w/a.ts","newUri":"file:///c:/w/b.ts"}]}""", json);
    }

    [Fact]
    public async Task WillRename_returns_server_edit_and_didRename_is_notified()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"editor-lsp-rename-{Guid.NewGuid():N}.ps1");
        var markerPath = Path.Combine(Path.GetTempPath(), $"editor-lsp-rename-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(scriptPath, """"
            param([string] $marker)
            $stdinStream = [Console]::OpenStandardInput()
            $stdoutStream = [Console]::OpenStandardOutput()

            function Read-Line([System.IO.Stream] $stream) {
                $builder = [System.Text.StringBuilder]::new()
                while ($true) {
                    $byte = $stream.ReadByte()
                    if ($byte -lt 0) { return $null }
                    if ($byte -eq 10) { return $builder.ToString() }
                    if ($byte -ne 13) { [void]$builder.Append([char]$byte) }
                }
            }

            function Send([string] $json) {
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
                $header = [System.Text.Encoding]::ASCII.GetBytes("Content-Length: $($bytes.Length)`r`n`r`n")
                $stdoutStream.Write($header, 0, $header.Length)
                $stdoutStream.Write($bytes, 0, $bytes.Length)
                $stdoutStream.Flush()
            }

            while ($true) {
                $length = $null
                while (($line = Read-Line $stdinStream) -ne '') {
                    if ($null -eq $line) { exit }
                    if ($line -match '^Content-Length:\s*(\d+)$') { $length = [int]$Matches[1] }
                }
                if ($null -eq $length) { exit }

                $body = [byte[]]::new($length)
                $read = 0
                while ($read -lt $length) {
                    $count = $stdinStream.Read($body, $read, $length - $read)
                    if ($count -le 0) { exit }
                    $read += $count
                }
                $message = ConvertFrom-Json ([System.Text.Encoding]::UTF8.GetString($body))

                if ($message.method -eq 'initialize') {
                    if (-not $message.params.capabilities.workspace.fileOperations.willRename) { exit 12 }
                    Send (('{"jsonrpc":"2.0","id":' + $message.id +
                        ',"result":{"capabilities":{"workspace":{"fileOperations":{"willRename":{"filters":' +
                        '[{"scheme":"file","pattern":{"glob":"**/*.ts","matches":"file"}}]}}}}}}'))
                }
                elseif ($message.method -eq 'workspace/willRenameFiles') {
                    if ($message.params.files[0].oldUri -ne 'file:///c:/w/a.ts' -or
                        $message.params.files[0].newUri -ne 'file:///c:/w/sub/a.ts') { exit 13 }
                    Send (('{"jsonrpc":"2.0","id":' + $message.id +
                        ',"result":{"changes":{"file:///c:/w/main.ts":[{"range":{"start":{"line":0,"character":20},' +
                        '"end":{"line":0,"character":25}},"newText":"./sub/a"}]}}}'))
                }
                elseif ($message.method -eq 'workspace/didRenameFiles') {
                    Set-Content -LiteralPath $marker -Value $message.params.files[0].newUri
                }
            }
            """", new System.Text.UTF8Encoding(false));

        try
        {
            using var client = new LspClient("powershell.exe", [
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, markerPath
            ]) { RequestTimeout = TimeSpan.FromSeconds(5) };
            await client.InitializeAsync("file:///c:/w/");

            var caps = Assert.IsType<JsonElement>(client.ServerCapabilities);
            Assert.True(caps.GetProperty("workspace").GetProperty("fileOperations").TryGetProperty("willRename", out _));

            var edit = await client.WillRenameFilesAsync([("file:///c:/w/a.ts", "file:///c:/w/sub/a.ts")]);

            Assert.NotNull(edit);
            var (uri, edits) = Assert.Single(edit!.Changes);
            Assert.Equal("file:///c:/w/main.ts", uri);
            Assert.Equal("./sub/a", Assert.Single(edits).NewText);

            await client.DidRenameFilesAsync([("file:///c:/w/a.ts", "file:///c:/w/sub/a.ts")]);
            for (var i = 0; i < 100 && !File.Exists(markerPath); i++)
                await Task.Delay(50);
            Assert.True(File.Exists(markerPath), "didRenameFiles が届いていない");
            Assert.Equal("file:///c:/w/sub/a.ts", File.ReadAllText(markerPath).Trim());
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
            try { File.Delete(markerPath); } catch { }
        }
    }
}
