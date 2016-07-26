using System;



namespace RmDropLimiter.CLI {



public class Program
{
    public static void Main(string[] args)
    {
        DropLimiter limiter = null;

        try
        {
            limiter = new DropLimiter();
        }
        catch (StateNotFoundException)
        {
            Console.WriteLine("No previous state file found, exiting " +
                              "without doing any drops. This is not an " +
                              "error, it is expected for the first run.");
            return;
        }

        limiter.Logger.Writers.Add(new ConsoleLogWriter());

        if (limiter.Config.ContainsKey("Log"))
            limiter.Logger.Writers.Add(
                new FileLogWriter(limiter.Config["Log"]));

        if (limiter.Config.ContainsKey("Report"))
            limiter.Report = new CsvReport(limiter.Config["Report"]);

        switch (limiter.Config.DatabasePatch)
        {
        case "auto":
            if (!DBPatch.IsInstalled(limiter.GameDB))
                DBPatch.Install(limiter.GameDB);

            break;

        case "prompt":
            if (!DBPatch.IsInstalled(limiter.GameDB))
            {
                Console.Write("rm-drop-limiter database patch not " +
                              "detected. Install now (y/n) ? ");

                if (Console.ReadLine().Trim().ToLower() != "y")
                    break;

                DBPatch.Install(limiter.GameDB);
            }

            break;

        case "disabled":
            if (DBPatch.IsInstalled(limiter.GameDB))
                DBPatch.Uninstall(limiter.GameDB);

            break;

        default:
            Console.WriteLine("In .conf file, DatabasePatch is set to [" +
                              limiter.Config.DatabasePatch + "], which is " +
                              "unrecognized. Exiting.");
            return;
        }

        var drops = new Drops();

        var newdrops = limiter.Run(drops);

        limiter.State.Save();

        foreach (var item in newdrops.Keys)
            Console.WriteLine("{0}: spawning {1} more.",
                              drops.FindByItemIndex(item).Description,
                              newdrops[item]);

        if (newdrops.Count > 0)
            limiter.GameDB.IncrementLimits(newdrops);
    }
}



}
