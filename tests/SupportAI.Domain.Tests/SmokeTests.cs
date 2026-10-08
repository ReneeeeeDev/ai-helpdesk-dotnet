namespace SupportAI.Domain.Tests;

public class SmokeTests
{
    [Fact]
    public void Domain_assembly_loads() => SupportAI.Domain.AssemblyReference.Assembly.ShouldNotBeNull();
}
