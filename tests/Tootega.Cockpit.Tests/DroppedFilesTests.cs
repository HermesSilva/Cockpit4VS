using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tootega.Cockpit.Protocol;
using Tootega.Cockpit.UI;
using Xunit;

namespace Tootega.Cockpit.Tests
{
    /// <summary>
    /// Files dropped on the composer: the real paths when WebView2 handed them over, a saved
    /// copy otherwise.
    /// </summary>
    public class DroppedFilesTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "drop-test-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        [Fact]
        public void RealPaths_WinOverContent()
        {
            var payload = new DropFilesPayload
            {
                Paths = new List<string> { @"C:\src\a.txt" },
                Files = new List<DroppedFilePayload> { new DroppedFilePayload { Name = "a.txt", Data = B64("x") } }
            };

            Assert.Equal(new[] { @"C:\src\a.txt" }, DroppedFiles.Resolve(payload, _root));
            Assert.False(Directory.Exists(_root));
        }

        [Fact]
        public void WithoutPaths_SavesCopies_UnderTheirNames()
        {
            var payload = new DropFilesPayload
            {
                Files = new List<DroppedFilePayload>
                {
                    new DroppedFilePayload { Name = "relatório.txt", Data = B64("olá") },
                    new DroppedFilePayload { Name = "big.bin" },
                    new DroppedFilePayload { Name = "relatório.txt", Data = B64("2") },
                    new DroppedFilePayload { Name = @"..\..\evil.txt", Data = B64("e") }
                }
            };

            var saved = DroppedFiles.Resolve(payload, _root);

            Assert.Equal(3, saved.Count);
            Assert.Equal("relatório.txt", Path.GetFileName(saved[0]));
            Assert.Equal("olá", File.ReadAllText(saved[0]));
            Assert.Equal("relatório (2).txt", Path.GetFileName(saved[1]));
            Assert.Equal("evil.txt", Path.GetFileName(saved[2]));
            Assert.StartsWith(_root, saved[2]);
        }
    }
}
