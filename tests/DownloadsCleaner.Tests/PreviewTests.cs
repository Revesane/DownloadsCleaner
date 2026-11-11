using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner.Tests;

public class PreviewTests
{
    [Theory]
    [InlineData(@"C:\Downloads\обычный.mp3")]
    [InlineData(@"C:\Downloads\100% готово.mp3")]
    [InlineData(@"C:\Downloads\file#1.mp3")]
    [InlineData(@"C:\Downloads\a&b.mp4")]
    public void File_uri_survives_special_characters_in_the_name(string path)
    {
        var uri = PreviewViewModel.TryCreateFileUri(path);

        Assert.NotNull(uri);
        Assert.True(uri!.IsFile);
        Assert.Equal(path, uri.LocalPath);
    }
}
