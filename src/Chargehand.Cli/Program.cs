// chargehand CLI. Exit codes: 0 ok, 1 run failed, 2 usage error, 3 needs input, 70 not implemented.
const string Usage = """
    usage:
      chargehand run  < request.json    reads request/v1 on stdin, writes result/v1 on stdout
      chargehand show <run-id>          prints a run from the run log
    """;

switch (args)
{
    case ["run"]:
    case ["show", _]:
        Console.Error.WriteLine("chargehand: not implemented yet (pre-alpha skeleton).");
        return 70;
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}
