using System.Globalization;

namespace QuickPanel.Updater;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 4 && args[0].Equals("--guard", StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateGuardian().Run(
                    args[1],
                    args[2],
                    int.Parse(args[3], CultureInfo.InvariantCulture));
            }
            if (args.Length == 4 && args[0].Equals("--recover", StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateGuardian().Recover(
                    args[1],
                    args[2],
                    int.Parse(args[3], CultureInfo.InvariantCulture));
            }
            if (args.Length == 4 && args[0].Equals("--apply", StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateTransactionRunner().Run(args[1], args[2], args[3]);
            }

            Console.Error.WriteLine("Usage: QuickPanel.Updater --apply <transaction> <state> <guardian> | --guard <transaction> <state> <runner-pid> | --recover <transaction> <state> <application-pid>");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }
}
