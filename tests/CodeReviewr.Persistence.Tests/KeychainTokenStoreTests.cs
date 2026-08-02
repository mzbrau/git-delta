using CodeReviewr.Persistence;
using NUnit.Framework;

namespace CodeReviewr.Persistence.Tests;

public sealed class KeychainTokenStoreTests
{
    [TestCase("line1\nline2")]
    [TestCase("line1\rline2")]
    public void SetTokenAsync_TokenWithNewline_ThrowsArgumentException(string token)
    {
        var store = new KeychainTokenStore();

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.SetTokenAsync("github.com", "user", token));
    }

    [Test]
    public void InterpretSecurityResult_Success_ReturnsStdout()
    {
        var result = KeychainTokenStore.InterpretSecurityResult(0, "secret-token\n", "", allowNotFound: false);
        Assert.That(result, Is.EqualTo("secret-token"));
    }

    [Test]
    public void InterpretSecurityResult_NotFound_Exit44_ReturnsNull_WhenAllowed()
    {
        var result = KeychainTokenStore.InterpretSecurityResult(
            KeychainTokenStore.ErrSecItemNotFound,
            "",
            "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain.",
            allowNotFound: true);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void InterpretSecurityResult_NotFound_Message_ReturnsNull_WhenAllowed()
    {
        var result = KeychainTokenStore.InterpretSecurityResult(
            1,
            "",
            "The specified item could not be found in the keychain.",
            allowNotFound: true);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void InterpretSecurityResult_NotFound_Throws_WhenNotAllowed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            KeychainTokenStore.InterpretSecurityResult(
                KeychainTokenStore.ErrSecItemNotFound,
                "",
                "could not be found",
                allowNotFound: false));
    }

    [Test]
    public void InterpretSecurityResult_OtherError_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            KeychainTokenStore.InterpretSecurityResult(1, "", "permission denied", allowNotFound: true));
        Assert.That(ex!.Message, Does.Contain("permission denied"));
    }

    [Test]
    public void IsNotFound_RecognizesExitCodeAndMessage()
    {
        Assert.That(KeychainTokenStore.IsNotFound(44, ""), Is.True);
        Assert.That(KeychainTokenStore.IsNotFound(1, "could not be found"), Is.True);
        Assert.That(KeychainTokenStore.IsNotFound(1, "other error"), Is.False);
    }
}
