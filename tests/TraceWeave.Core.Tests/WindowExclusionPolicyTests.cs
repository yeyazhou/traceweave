using TraceWeave.Infrastructure.Windows;

namespace TraceWeave.Core.Tests;

public sealed class WindowExclusionPolicyTests
{
    [Fact]
    public void TraceWeaveProcessIsAlwaysExcludedToPreventSelfCapture()
    {
        var policy = new DefaultWindowExclusionPolicy();

        Assert.True(policy.IsExcluded("TraceWeave.App", "TraceWeave"));
        Assert.True(policy.IsExcluded("TraceWeave.App.exe", "TraceWeave"));
    }

    [Fact]
    public void CredentialAndPasswordManagerWindowsRemainExcluded()
    {
        var policy = new DefaultWindowExclusionPolicy();

        Assert.True(policy.IsExcluded("example.exe", "Credential Manager"));
        Assert.True(policy.IsExcluded("example.exe", "Password Manager"));
    }
}
