using BossMod;

// usage: TtkEval survey <out.csv> <replay.log|dir>...        per-pull evidence (kill/wipe, HP, director updates) for choosing the kill rule
//        TtkEval extract <outDir> [--jobs N] <replay.log|dir>...   writes one compact .ttk file per replay (HP feed series of kill pulls)
//        TtkEval eval ...                                    (see Eval.cs)
//        TtkEval selftest                                    estimator self test
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: TtkEval <survey|extract|eval|selftest> ...");
            return 2;
        }
        switch (args[0])
        {
            case "selftest": return SelfTest.Run();
            case "survey":
            case "extract":
                Init();
                return Extract.Run(args);
            case "eval": Init(); return Eval.Run(args);
            case "live": return Live.Run(args);
            case "bench": Init(); return Bench.Run(args[1]);
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 2;
        }
    }

    public static void Init()
    {
        // Building a WorldState touches ActionDefinitions, which reads game sheets: same setup as tools/replay_timeline_extract.
        Service.LuminaGameData = new Lumina.GameData(@"C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
        Service.Config.Initialize();
        Service.LogHandlerDebug = msg =>
        {
            if (msg.StartsWith("Failed to read", StringComparison.Ordinal))
                Console.Error.WriteLine(msg);
        };
    }
}
