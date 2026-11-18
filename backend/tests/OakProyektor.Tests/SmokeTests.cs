namespace OakProyektor.Tests;

public class SmokeTests
{
    [Fact]
    public void Assemblies_Load()
    {
        Assert.NotNull(typeof(OakProyektor.Core.Output.OutputState).Assembly);
        Assert.NotNull(typeof(OakProyektor.Data.DatabaseInitializer).Assembly);
        Assert.NotNull(typeof(OakProyektor.Ipc.IpcDispatcher).Assembly);
    }
}


