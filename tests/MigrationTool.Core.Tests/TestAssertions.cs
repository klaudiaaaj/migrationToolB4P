namespace MigrationTool.Core.Tests;

internal static class TestAssertions
{
    public static void DoesNotThrow(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"Oczekiwano braku wyjątku, ale otrzymano {exception.GetType().Name}: {exception.Message}");
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"Oczekiwano {typeof(TException).Name}, ale otrzymano {exception.GetType().Name}: {exception.Message}");
        }

        Assert.Fail($"Oczekiwano wyjątku {typeof(TException).Name}, ale nie został rzucony.");
        throw new InvalidOperationException("Kod nieosiągalny.");
    }
}
