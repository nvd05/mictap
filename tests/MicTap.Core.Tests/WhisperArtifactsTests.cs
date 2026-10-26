namespace MicTap.Core.Tests;

public sealed class WhisperArtifactsTests
{
    [Theory]
    [InlineData("Продолжение следует...")]
    [InlineData("  продолжение следует… ")]
    [InlineData("Субтитры сделал DimaTorzok")]
    [InlineData("Субтитры создавал DimaTorzok")]
    [InlineData("Редактор субтитров А.Синецкая Корректор А.Егорова")]
    [InlineData("Спасибо за просмотр!")]
    [InlineData("Подписывайтесь на канал!")]
    [InlineData("ご視聴ありがとうございました。")]
    public void Known_made_up_phrases_are_artifacts(string text)
    {
        Assert.True(WhisperArtifacts.IsArtifact(text));
    }

    [Theory]
    [InlineData("Продолжение следует на следующей паре, а сейчас перерыв.")]
    [InlineData("Спасибо за внимание.")]
    [InlineData("Субтитры")]
    [InlineData("Сегодня разберём интегралы.")]
    [InlineData("")]
    public void Speech_that_only_contains_or_resembles_them_is_kept(string text)
    {
        Assert.False(WhisperArtifacts.IsArtifact(text));
    }
}
