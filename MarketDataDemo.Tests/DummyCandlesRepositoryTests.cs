using MarketDataDemo.Candles;

namespace MarketDataDemo.Tests;

public class DummyCandlesRepositoryTests
{
    [Fact]
    public async Task GetAndRemoveMethods_DoNotThrow()
    {
        var repo = new DummyCandlesRepository();
        var files = await repo.GetCandleFilesAsync("SYM", 5);
        Assert.Empty(files);
        await repo.RemoveCandleFilesAsync("SYM", 5);
        await repo.RemoveCandleFileAsync(new CandleFileInfo { Path = "dummy", Start = DateTime.UtcNow, End = DateTime.UtcNow, Version = 1 });
    }
}
