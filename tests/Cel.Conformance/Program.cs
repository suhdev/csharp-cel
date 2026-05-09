using System;
using System.IO;
using Cel.Conformance;

// CLI: dotnet run --project tests/Cel.Conformance -- [path to testdata] [--only file1 file2 ...]
//
// Path defaults to ../../cel-spec/tests/simple/testdata relative to the cel-csharp repo root.
// Outputs a per-file summary table plus first-failure samples.

string testdata;
List<string>? onlyFiles = null;

if (args.Length == 0)
{
    var defaultPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "cel-spec", "tests", "simple", "testdata"));
    testdata = defaultPath;
}
else
{
    testdata = args[0];
    var i = 1;
    while (i < args.Length)
    {
        if (args[i] == "--only")
        {
            onlyFiles = new List<string>();
            for (var j = i + 1; j < args.Length; j++) { onlyFiles.Add(args[j]); }
            break;
        }
        i++;
    }
}

if (!Directory.Exists(testdata))
{
    Console.Error.WriteLine($"testdata directory not found: {testdata}");
    return 2;
}

Console.WriteLine($"Running conformance tests from {testdata}");
var results = ConformanceRunner.Run(testdata, onlyFiles);

Console.WriteLine();
Console.WriteLine($"{"file",-28} {"total",6} {"pass",6} {"fail",6} {"skip",6}  rate");
Console.WriteLine(new string('-', 65));
var grandTotal = 0;
var grandPass = 0;
var grandFail = 0;
var grandSkip = 0;
foreach (var r in results)
{
    var ran = r.Total - r.Skipped;
    var rate = ran == 0 ? 0 : (int)Math.Round(100.0 * r.Passed / ran);
    Console.WriteLine($"{r.FileName,-28} {r.Total,6} {r.Passed,6} {r.Failed,6} {r.Skipped,6}  {rate,3}%");
    grandTotal += r.Total;
    grandPass += r.Passed;
    grandFail += r.Failed;
    grandSkip += r.Skipped;
}
Console.WriteLine(new string('-', 65));
var grandRan = grandTotal - grandSkip;
var grandRate = grandRan == 0 ? 0 : (int)Math.Round(100.0 * grandPass / grandRan);
Console.WriteLine($"{"TOTAL",-28} {grandTotal,6} {grandPass,6} {grandFail,6} {grandSkip,6}  {grandRate,3}%");

Console.WriteLine();
Console.WriteLine("First failures per file:");
foreach (var r in results)
{
    if (r.FailureSamples.Count == 0) { continue; }
    Console.WriteLine();
    Console.WriteLine($"  {r.FileName}:");
    foreach (var f in r.FailureSamples)
    {
        Console.WriteLine($"    [{f.Section}/{f.Name}] {f.Detail}");
    }
}
return grandFail == 0 ? 0 : 1;
