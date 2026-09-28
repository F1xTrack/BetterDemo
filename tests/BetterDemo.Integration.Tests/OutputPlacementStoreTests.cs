using BetterDemo.Interop.Win32;
using Xunit;

namespace BetterDemo.Integration.Tests;

public sealed class OutputPlacementStoreTests
{
    [Fact]
    public void Position_round_trips_and_invalid_data_is_ignored()
    {
        var path = Path.GetTempFileName();
        try
        {
            var store = new OutputPlacementStore(path);
            File.WriteAllText(path, "not JSON");
            Assert.Null(store.Load());
            File.WriteAllText(path, "{\"x\":100001,\"y\":10}");
            Assert.Null(store.Load());
            File.WriteAllText(path, "{\"x\":\"200\",\"y\":10}");
            Assert.Null(store.Load());

            var expected = new OutputWindowPosition(-640, 120);
            store.Save(expected);
            Assert.Equal(expected, store.Load());
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(new OutputWindowPosition(int.MaxValue, 0)));
            Assert.Equal(expected, store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
