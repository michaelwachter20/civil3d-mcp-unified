using System.Text;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using Civil3DMcpPlugin;

var testRoot = Path.Combine(Path.GetTempPath(), $"civil3d-mcp-file-boundary-{Guid.NewGuid():N}");
var allowedRoot = Path.Combine(testRoot, "allowed");
var outsideRoot = Path.Combine(testRoot, "outside");
Directory.CreateDirectory(allowedRoot);
Directory.CreateDirectory(outsideRoot);

Environment.SetEnvironmentVariable("CIVIL3D_IMPORT_ROOTS", allowedRoot);
Environment.SetEnvironmentVariable("CIVIL3D_EXPORT_ROOTS", allowedRoot);

try
{
  // Every loop a script can see gets a cancellation check, without adding a line, so the timeout can stop a tight loop
  // and errors keep the script's own line numbers.
  const string LoopCheck = "CancellationToken.ThrowIfCancellationRequested();";
  static int Checks(string code) => code.Split(LoopCheck).Length - 1;
  Assert(Checks(ScriptLoopGuard.AddChecks("while (true) { }")) == 1, "A while loop got no check.");
  Assert(Checks(ScriptLoopGuard.AddChecks("do { } while (true);")) == 1, "A do loop got no check.");
  Assert(Checks(ScriptLoopGuard.AddChecks("foreach (var x in new[] { 1 }) { }")) == 1, "A foreach loop got no check.");
  Assert(ScriptLoopGuard.AddChecks("for (var i = 0; i < 3; i++) i += 0;")
    == "for (var i = 0; i < 3; i++) { " + LoopCheck + " i += 0; }", "A one-line loop body wasn't wrapped.");
  Assert(Checks(ScriptLoopGuard.AddChecks("while (true) for (;;) while (true);")) == 3, "Nested one-line loops were missed.");
  var linesKept = ScriptLoopGuard.AddChecks("var i = 0;\nwhile (i < 3)\n{\n  i++;\n}\nreturn i;");
  Assert(linesKept.Split('\n').Length == 6, "The guard changed the script's line numbers.");
  foreach (var untouched in new[]
  {
    "return 1;",
    "var words = \"while (true) { }\"; // for (;;) { }\nreturn words;",
    "class Helper { public void F() { while (true) { } } }",
    "System.Action run = static () => { while (true) { } };",
    "while (true) {",
  })
  {
    Assert(ScriptLoopGuard.AddChecks(untouched) == untouched, $"The guard changed: {untouched}");
  }

  // What it's for: a script that would never end stops when its token is cancelled, as the timeout does in Civil 3D.
  foreach (var endless in new[]
  {
    "while (true) { }",
    "for (;;) ;",
    "var n = 0; do { n++; } while (n >= 0 || n < 0);",
    "System.Action spin = () => { while (true) { } }; spin();",
    "return await System.Threading.Tasks.Task.Run(() => { while (true) { } return 1; });",
  })
  {
    using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    var scriptRun = Task.Run(() => Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript.RunAsync(
      ScriptLoopGuard.AddChecks(endless), globals: new ScriptGlobals(stop.Token), globalsType: typeof(ScriptGlobals)));
    if (await Task.WhenAny(scriptRun, Task.Delay(TimeSpan.FromSeconds(20))) != scriptRun)
    {
      throw new InvalidOperationException($"The loop guard didn't stop: {endless}");
    }

    Assert(scriptRun.IsCanceled || scriptRun.Exception?.GetBaseException() is OperationCanceledException,
      $"The loop guard ended '{endless}' with {scriptRun.Exception?.GetBaseException().GetType().Name ?? "no exception"}.");
  }

  var nestedOutput = Path.Combine(allowedRoot, "reports", "quantities.csv");
  var canonical = FileBoundary.ResolveExportPath(nestedOutput, overwrite: false, ".csv");
  Assert(canonical == Path.GetFullPath(nestedOutput), "Allowed output was not canonicalized.");

  ExpectCode(
    "CIVIL3D.PATH_NOT_ALLOWED",
    () => FileBoundary.ResolveExportPath(Path.Combine(allowedRoot, "..", "outside", "escape.csv"), false, ".csv"));
  ExpectCode(
    "CIVIL3D.FILE_TYPE_NOT_ALLOWED",
    () => FileBoundary.ResolveExportPath(Path.Combine(allowedRoot, "report.exe"), false, ".csv"));

  var writtenPath = FileBoundary.WriteAllTextAtomic(
    nestedOutput, "first", Encoding.UTF8, overwrite: false, ".csv");
  Assert(File.ReadAllText(writtenPath, Encoding.UTF8) == "first", "Atomic write content mismatch.");
  Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(writtenPath)!, ".*.tmp").Any(), "Atomic write left a temp file.");

  ExpectCode(
    "CIVIL3D.CONFLICT",
    () => FileBoundary.WriteAllTextAtomic(nestedOutput, "blocked", Encoding.UTF8, overwrite: false, ".csv"));
  FileBoundary.WriteAllTextAtomic(nestedOutput, "replacement", Encoding.UTF8, overwrite: true, ".csv");
  Assert(File.ReadAllText(writtenPath, Encoding.UTF8) == "replacement", "Explicit overwrite did not replace content.");

  var importPath = Path.Combine(allowedRoot, "terrain.dem");
  File.WriteAllText(importPath, "dem-data");
  Assert(FileBoundary.ResolveImportPath(importPath, ".dem") == Path.GetFullPath(importPath), "Allowed import was rejected.");
  ExpectCode(
    "CIVIL3D.OBJECT_NOT_FOUND",
    () => FileBoundary.ResolveImportPath(Path.Combine(allowedRoot, "missing.dem"), ".dem"));
  ExpectCode(
    "CIVIL3D.PATH_NOT_ALLOWED",
    () => FileBoundary.ResolveImportPath(Path.Combine(outsideRoot, "outside.dem"), ".dem"));

  // The suite's shared folder (job databases, backups, User Knowledge) is reference only: never a source for an import
  var sharedRoot = Path.Combine(allowedRoot, "Project Knowledge");
  Directory.CreateDirectory(Path.Combine(sharedRoot, "projects"));
  var sharedImport = Path.Combine(sharedRoot, "projects", "copied-points.dem");
  File.WriteAllText(sharedImport, "dem-data");
  Environment.SetEnvironmentVariable("PROJECT_KNOWLEDGE_DIR", sharedRoot);
  ExpectCode("CIVIL3D.PATH_NOT_ALLOWED", () => FileBoundary.ResolveImportPath(sharedImport, ".dem"));
  // The user's own standards in a job's User Knowledge folder: readable for reference, never imported as data.
  var standards = Path.Combine(sharedRoot, "projects", "elm-street", "User Knowledge");
  Directory.CreateDirectory(standards);
  var standard = Path.Combine(standards, "Standard details.dwg");
  File.WriteAllText(standard, "dwg");
  Assert(FileBoundary.ResolveReferencePath(standard, ".dwg") == Path.GetFullPath(standard), "A User Knowledge standard was refused as a go-by.");
  ExpectCode("CIVIL3D.PATH_NOT_ALLOWED", () => FileBoundary.ResolveImportPath(standard, ".dwg"));
  var notStandards = Path.Combine(sharedRoot, "projects", "elm-street.backups", "copy.dwg");
  Directory.CreateDirectory(Path.GetDirectoryName(notStandards)!);
  File.WriteAllText(notStandards, "dwg");
  ExpectCode("CIVIL3D.PATH_NOT_ALLOWED", () => FileBoundary.ResolveReferencePath(notStandards, ".dwg"));
  Assert(FileBoundary.ResolveImportPath(importPath, ".dem") == Path.GetFullPath(importPath), "An import next to the shared folder was rejected.");
  Environment.SetEnvironmentVariable("PROJECT_KNOWLEDGE_DIR", null);

  var scriptsRoot = Path.Combine(testRoot, "scripts");
  var scriptPath = FileBoundary.WriteFileWithinDirectoryAtomic(scriptsRoot, "relayer.csx", "v1", overwrite: false);
  Assert(scriptPath == Path.Combine(Path.GetFullPath(scriptsRoot), "relayer.csx"), "Script write resolved to an unexpected path.");
  Assert(File.ReadAllText(scriptPath) == "v1", "Script write content mismatch.");
  Assert(!Directory.EnumerateFiles(scriptsRoot, ".*.tmp").Any(), "Script write left a temp file.");
  ExpectCode("CIVIL3D.CONFLICT", () => FileBoundary.WriteFileWithinDirectoryAtomic(scriptsRoot, "relayer.csx", "v2", overwrite: false));
  FileBoundary.WriteFileWithinDirectoryAtomic(scriptsRoot, "relayer.csx", "v2", overwrite: true);
  Assert(File.ReadAllText(scriptPath) == "v2", "Script overwrite did not replace content.");
  foreach (var badName in new[] { "../escape.csx", "a/b.csx", "a\\b.csx", "..", "", " ", Path.Combine(outsideRoot, "abs.csx") })
  {
    ExpectCode("CIVIL3D.INVALID_INPUT", () => FileBoundary.WriteFileWithinDirectoryAtomic(scriptsRoot, badName, "x", overwrite: true));
  }
  Assert(!File.Exists(Path.Combine(testRoot, "escape.csx")), "Traversal write escaped the directory.");
  Assert(FileBoundary.DeleteFileWithinDirectory(scriptsRoot, "relayer.csx"), "Script delete reported missing file.");
  Assert(!File.Exists(scriptPath), "Script delete left the file.");
  Assert(!FileBoundary.DeleteFileWithinDirectory(scriptsRoot, "relayer.csx"), "Deleting a missing script reported success.");
  ExpectCode("CIVIL3D.INVALID_INPUT", () => FileBoundary.DeleteFileWithinDirectory(scriptsRoot, "../allowed/terrain.dem"));
  Assert(File.Exists(importPath), "Traversal delete removed a file outside the directory.");

  var rpcError = JsonNode.Parse(JsonRpcProtocol.SerializeError(
    JsonValue.Create("request-1"),
    JsonRpcProtocol.NumericErrorCode("CIVIL3D.OBJECT_NOT_FOUND"),
    "CIVIL3D.OBJECT_NOT_FOUND",
    "Surface was not found"))!.AsObject();
  Assert(rpcError["jsonrpc"]!.GetValue<string>() == "2.0", "JSON-RPC version is invalid.");
  Assert(rpcError["error"]!["code"]!.GetValue<int>() == -32004, "JSON-RPC error code is not numeric.");
  Assert(rpcError["error"]!["data"]!["code"]!.GetValue<string>() == "CIVIL3D.OBJECT_NOT_FOUND", "Domain error code was not retained in error.data.code.");

  var completedJob = JobRegistry.Create("Queued", "bulk_qc_report", "request-complete", "drawing-a");
  JobRegistry.Progress(completedJob.JobId, 42, "Working", 10);
  JobRegistry.Complete(completedJob.JobId, new { report = "ok" }, new[] { "review warning" });
  var completed = JobRegistry.Get(completedJob.JobId);
  Assert(completed.State == "completed" && completed.ProgressPercent == 100, "Completed job state was invalid.");
  Assert(completed.DurationMs is >= 0 && completed.RequestId == "request-complete", "Completed job telemetry was missing.");
  Assert(completed.Warnings.Count == 1, "Completed job warnings were not retained.");

  var failedJob = JobRegistry.Create("Queued", "surface_dem_import", "request-fail", "drawing-b");
  JobRegistry.Fail(failedJob.JobId, "Import failed", "validation");
  var failed = JobRegistry.Get(failedJob.JobId);
  Assert(failed.State == "failed" && failed.FailureCategory == "validation", "Failure telemetry was invalid.");

  var cancelledJob = JobRegistry.Create("publish_sheet_pdf");
  JobRegistry.Cancel(cancelledJob.JobId);
  Assert(JobRegistry.Get(cancelledJob.JobId).State == "running" && JobRegistry.Get(cancelledJob.JobId).CancellationRequested, "Cancellation request was not retained while work remained in flight.");
  JobRegistry.AcknowledgeCancellation(cancelledJob.JobId);
  Assert(JobRegistry.Get(cancelledJob.JobId).State == "cancelled", "Cancelled job state was invalid.");

  var stats = JobRegistry.GetStats();
  Assert(stats.Completed >= 1 && stats.Failed >= 1 && stats.Cancelled >= 1, "Job registry statistics were invalid.");
  Assert(stats.Total <= stats.Capacity, "Job registry exceeded its bounded capacity.");

  for (var iteration = 0; iteration < 25; iteration++)
  {
    var raceJob = JobRegistry.Create("Race test");
    await Task.WhenAll(
      Task.Run(() => JobRegistry.Complete(raceJob.JobId, new { ok = true })),
      Task.Run(() => { JobRegistry.Cancel(raceJob.JobId); JobRegistry.AcknowledgeCancellation(raceJob.JobId); }));
    var terminalRaceJob = JobRegistry.Get(raceJob.JobId);
    Assert(terminalRaceJob.State is "completed" or "cancelled", "Racing terminal transitions produced an invalid job state.");
    Assert(terminalRaceJob.CompletedAt.HasValue && terminalRaceJob.DurationMs is >= 0, "Racing terminal transition lost timing telemetry.");
  }

  var capacityRejected = false;
  for (var index = 0; index <= stats.Capacity + 4; index++)
  {
    try
    {
      JobRegistry.Create("Capacity test");
    }
    catch (JsonRpcDispatchException exception) when (exception.Code == "CIVIL3D.HOST_BUSY")
    {
      capacityRejected = true;
      break;
    }
  }
  Assert(capacityRejected, "Job registry did not reject work at its bounded capacity.");
  Assert(JobRegistry.GetStats().Total <= stats.Capacity, "Job registry exceeded capacity under pressure.");

  var disconnectPort = GetFreeTcpPort();
  var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  var rpcServer = new RpcTcpServer(disconnectPort, async (_, cancellationToken) =>
  {
    handlerStarted.TrySetResult();
    try
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      return "{}";
    }
    catch (OperationCanceledException)
    {
      handlerCancelled.TrySetResult();
      throw;
    }
  });
  rpcServer.Start();
  try
  {
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, disconnectPort);
    var requestBytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"wait\",\"id\":\"disconnect-test\"}");
    await client.GetStream().WriteAsync(requestBytes);
    await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    client.Dispose();
    await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
  }
  finally
  {
    rpcServer.Stop();
  }

  var responsePort = GetFreeTcpPort();
  var responseServer = new RpcTcpServer(responsePort, (_, _) => Task.FromResult("{\"ok\":true}"));
  responseServer.Start();
  try
  {
    using var responseClient = new TcpClient();
    await responseClient.ConnectAsync(IPAddress.Loopback, responsePort);
    var requestBytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":\"response-test\"}");
    await responseClient.GetStream().WriteAsync(requestBytes);
    var responseBuffer = new byte[128];
    var responseLength = await responseClient.GetStream().ReadAsync(responseBuffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    Assert(Encoding.UTF8.GetString(responseBuffer, 0, responseLength) == "{\"ok\":true}", "RPC response path stalled while stopping the disconnect monitor.");
  }
  finally
  {
    responseServer.Stop();
  }

  // Non-UTF-8 bytes (a Windows-1252 degree sign) are rejected with a clear error instead of becoming U+FFFD.
  var utf8Port = GetFreeTcpPort();
  var handlerSawBadRequest = false;
  var utf8Server = new RpcTcpServer(utf8Port, (request, _) => { handlerSawBadRequest = true; return Task.FromResult("{}"); });
  utf8Server.Start();
  try
  {
    using var utf8Client = new TcpClient();
    await utf8Client.ConnectAsync(IPAddress.Loopback, utf8Port);
    var badBytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"compileCode\",\"params\":{\"code\":\"return \\\"N89")
      .Concat(new byte[] { 0xB0 })
      .Concat(Encoding.UTF8.GetBytes("33\\\";\"}}"))
      .ToArray();
    await utf8Client.GetStream().WriteAsync(badBytes);
    var buffer = new byte[1024];
    var length = await utf8Client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    var reply = Encoding.UTF8.GetString(buffer, 0, length);
    Assert(reply.Contains("not valid UTF-8") && reply.Contains("CIVIL3D.INVALID_REQUEST"), $"Invalid UTF-8 was not rejected clearly: {reply}");
    Assert(!handlerSawBadRequest, "A request with invalid UTF-8 reached the dispatcher.");
  }
  finally
  {
    utf8Server.Stop();
  }

  // Point text (importCogoPoints / exportCogoPoints): X is the easting, Y the northing, in every format.
  var pnezd = PointText.Parse("101,5000.25,2000.5,100.1,EP 12\n\n102;5001;2001;101", "pnezd").ToList();
  Assert(pnezd.Count == 2 && pnezd[0] == new PointText.ParsedPoint(101, 2000.5, 5000.25, 100.1, "EP 12")
         && pnezd[1].Description == "" && pnezd[1].Number == 102,
    "pnezd: northing then easting, the number kept, a description with a space kept whole.");
  var penz = PointText.Parse("7 2000.5 5000.25 100.1", "penz").Single();
  Assert(penz.X == 2000.5 && penz.Y == 5000.25 && penz.Z == 100.1, "penz: easting, northing, elevation.");
  var xyz = PointText.Parse("2000.5\t5000.25\t100.1", "xyz").Single();
  Assert(xyz.Number == null && xyz.X == 2000.5 && xyz.Y == 5000.25, "xyz has no point numbers.");
  ExpectCode("CIVIL3D.INVALID_INPUT", () => PointText.Parse("1,5000,2000,abc", "pnezd").ToList());
  ExpectCode("CIVIL3D.INVALID_INPUT", () => PointText.Parse("1,5000,2000", "pnezd").ToList());
  var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
  System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
  try
  {
    var line = PointText.Format("pnezd", 101, null, 2000.5, 5000.25, 100.1, "EP 12", ",");
    Assert(line == "101,5000.25,2000.5,100.1,EP 12", $"pnezd export is northing then easting with dot decimals: {line}");
    var back = PointText.Parse(line, "pnezd").Single();
    Assert(back.X == 2000.5 && back.Y == 5000.25, "A point exported and imported again keeps its easting and northing.");
    Assert(PointText.Format("penz", 7, null, 2000.5, 5000.25, 100.1, "", ",") == "7,2000.5,5000.25,100.1,",
      "penz export is easting, northing, elevation.");
  }
  finally
  {
    System.Globalization.CultureInfo.CurrentCulture = previousCulture;
  }

  // Pipe arithmetic (PipeMath): slope from the ends, Manning's in feet and meters, clearances between pipes.
  Assert(Math.Abs(PipeMath.SlopePercent(0, 0, 100, 300, 400, 95)!.Value - 1.0) < 1e-9,
    "A pipe falling 5 over 500 horizontally slopes 1%.");
  Assert(PipeMath.SlopePercent(1, 1, 10, 1, 1, 5) == null, "A vertical pipe has no slope.");
  // 12-inch (1 ft) pipe at 1%, n = 0.013: about 3.56 cfs full; 0.3 m pipe at 1%: about 0.0966 cms.
  Assert(Math.Abs(PipeMath.FullFlowCapacity(1.0, 1.0, 0.013, "feet") - 3.564) < 0.01,
    $"Manning's in feet: {PipeMath.FullFlowCapacity(1.0, 1.0, 0.013, "feet")}");
  Assert(Math.Abs(PipeMath.FullFlowCapacity(0.3, 1.0, 0.013, "meters") - 0.0966) < 0.001,
    $"Manning's in meters: {PipeMath.FullFlowCapacity(0.3, 1.0, 0.013, "meters")}");
  var crossing = PipeMath.SegmentDistance((0, 0, 100), (10, 0, 100), (5, -5, 101), (5, 5, 101));
  Assert(Math.Abs(crossing.Distance - 1.0) < 1e-9 && Math.Abs(crossing.OnFirst - 0.5) < 1e-9,
    "Two pipes crossing 1 apart vertically, at the middle of the first.");
  var apart = PipeMath.SegmentDistance((0, 0, 0), (10, 0, 0), (20, 3, 0), (30, 3, 0));
  Assert(Math.Abs(apart.Distance - Math.Sqrt(100 + 9)) < 1e-9, "End to end: from the end of one to the start of the other.");
  var parallel = PipeMath.SegmentDistance((0, 0, 0), (10, 0, 0), (2, 4, 0), (8, 4, 0));
  Assert(Math.Abs(parallel.Distance - 4.0) < 1e-9, "Two parallel pipes 4 apart.");
  var samples = PipeMath.SampleParameters(12, 5);
  Assert(samples.Count == 4 && samples[0] == 0 && samples[^1] == 1, "A 12-unit pipe sampled every 5: 0, 1/3, 2/3, 1.");

  // A section keeps its ends and thins evenly (StationMath.EvenIndices).
  Assert(StationMath.EvenIndices(5, 200).SequenceEqual(new[] { 0, 1, 2, 3, 4 }), "A short section is left alone.");
  var thinned = StationMath.EvenIndices(1377, 200);
  Assert(thinned.Count == 200 && thinned[0] == 0 && thinned[^1] == 1376 && thinned.SequenceEqual(thinned.OrderBy(index => index)), "A long one keeps its ends, in order.");
  Assert(StationMath.EvenIndices(0, 200).Count == 0, "No points gives none.");

  // A long call and low memory are answered, not waited on (HostGuard).
  Assert(HostGuard.BusyRefusal(null, 999_000) == null, "Nothing running refuses nothing.");
  Assert(HostGuard.BusyRefusal("checkStoppingDistance", 59_000) == null, "A call under a minute lets the next one wait.");
  Assert(HostGuard.BusyRefusal("checkStoppingDistance", 95_000) is { } busy && busy.Contains("checkStoppingDistance") && busy.Contains("95 s") && busy.Contains("Not Responding"),
    "A long call is named, with its time, and says it isn't a crash.");
  Assert(HostGuard.MemoryRefusal(299) != null && HostGuard.MemoryRefusal(300) == null, "New work stops below 300 MB of commit.");
  var readings = new Queue<long>(new long[] { 250, 260, 1650 });
  var waits = 0;
  Assert(HostGuard.CheckMemory(() => readings.Dequeue(), () => waits++) == null && waits == 2, "A dip that recovers refuses nothing: Windows grew its page file.");
  waits = 0;
  Assert(HostGuard.CheckMemory(() => 250, () => waits++) != null && waits == 6, "Memory that stays low is refused, after six checks.");
  waits = 0;
  Assert(HostGuard.CheckMemory(() => 2000, () => waits++) == null && waits == 0, "Plenty of memory costs no wait.");
  Assert(HostGuard.MemoryRefusal(-1) == null, "When Windows won't say, nothing is refused.");
  Assert(HostGuard.FreeCommitMb() > 0, "Windows reports the commit left.");

  // AASHTO's published stopping and passing distances, which FDOT's manual repeats (SightMath).
  (double Mph, double Stopping, double Passing)[] aashto =
    [(25, 155, 450), (30, 200, 500), (35, 250, 550), (40, 305, 600), (45, 360, 700), (50, 425, 800), (55, 495, 900), (60, 570, 1000), (65, 645, 1100), (70, 730, 1200)];
  foreach (var (speed, stopping, passing) in aashto)
  {
    Assert(SightMath.StoppingFeet(speed) == stopping, $"Stopping at {speed} mph is {stopping} ft: {SightMath.StoppingFeet(speed)}");
    Assert(SightMath.PassingDistanceFeet(speed) == passing, $"Passing at {speed} mph is {passing} ft.");
  }
  (double Kmh, double Metres)[] aashtoMetric = [(50, 65), (60, 85), (80, 130), (100, 185), (120, 250)];
  foreach (var (kmh, metres) in aashtoMetric)
  {
    Assert(SightMath.StoppingMetres(kmh) == metres, $"Stopping at {kmh} km/h is {metres} m: {SightMath.StoppingMetres(kmh)}");
  }
  Assert(Math.Abs(SightMath.StoppingFeet(40, -3) - 315) < 1 && Math.Abs(SightMath.StoppingFeet(40, 3) - 289) < 1,
    $"Downhill takes longer than uphill: {SightMath.StoppingFeet(40, -3)}, {SightMath.StoppingFeet(40, 3)}");
  Assert(SightMath.PassingDistanceFeet(10) == 400 && SightMath.PassingDistanceFeet(90) == 1400 && SightMath.PassingDistanceFeet(42.5) == 650,
    "Passing distance interpolates, and holds at the ends of the table.");

  // Stations every interval (StationMath): the end kept despite rounding, names with a dot on any PC.
  var tenths = StationMath.IntervalStations(0, 1, 0.1);
  Assert(tenths.Count == 11 && tenths[^1] == 1, $"0 to 1 every 0.1 is 11 stations ending at 1: {tenths.Count}");
  var run = StationMath.IntervalStations(1000, 1262.5, 25);
  Assert(run.Count == 11 && run[^1] == 1250, "1000 to 1262.5 every 25 stops at 1250.");
  Assert(StationMath.IntervalStations(0, 100, 0).Count == 0 && StationMath.IntervalStations(100, 0, 10).Count == 0,
    "No stations for a zero interval or an end before the start.");
  // Half full carries half the flow at full velocity, the textbook check on the part-full curve.
  var halfFull = PipeMath.PartFull(0.5);
  Assert(Math.Abs(halfFull.DepthRatio - 0.5) < 0.005 && Math.Abs(halfFull.AreaRatio - 0.5) < 0.01 && Math.Abs(halfFull.TopWidthRatio - 1) < 0.01,
    $"Half the flow runs about half full: y/D {halfFull.DepthRatio}.");
  var tenth = PipeMath.PartFullFlow(1, 10, 2, "feet");
  var fullVelocity = 10 / (Math.PI * 2 * 2 / 4);
  Assert(tenth.Velocity > 0.55 * fullVelocity && tenth.Velocity < 0.7 * fullVelocity && tenth.DepthRatio > 0.2 && tenth.DepthRatio < 0.25,
    $"A tenth of capacity runs near 0.6 of full velocity: {tenth.Velocity / fullVelocity}.");
  Assert(PipeMath.PartFull(1.2).DepthRatio == 1 && PipeMath.PartFullFlow(12, 10, 2, "feet").Froude == 0, "Over capacity counts as full.");
  var cultureBefore = System.Globalization.CultureInfo.CurrentCulture;
  try
  {
    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
    Assert(StationMath.SampleLineName(1262.5) == "SL-1262.5" && StationMath.SampleLineName(1250) == "SL-1250",
      $"Sample line names use a dot: {StationMath.SampleLineName(1262.5)}");
    Assert(StationMath.SampleLineName("SLG", 100.001) != StationMath.SampleLineName("SLG", 100.004)
      && StationMath.SampleLineName("SLG", 100) != StationMath.SampleLineName("SLG2", 100),
      "Sample line names differ between close stations and between groups.");
  }
  finally
  {
    System.Globalization.CultureInfo.CurrentCulture = cultureBefore;
  }

  // What counts as the AI making points (PointCreationRules): those ask the user in Civil 3D first.
  Assert(PointCreationRules.ScriptAddsPoints("var id = CivilDoc.CogoPoints.Add(new Point3d(e, n, z), true);")
         && PointCreationRules.ScriptAddsPoints("var pts = CivilDoc.CogoPoints;\nforeach (var p in list) pts.Add(p, true);"),
    "A script adding COGO points, directly or through a variable, counts.");
  Assert(!PointCreationRules.ScriptAddsPoints("foreach (ObjectId id in CivilDoc.CogoPoints) Count++;")
         && !PointCreationRules.ScriptAddsPoints("names.Add(alignment.Name);"),
    "A script that only reads points, or adds to a list without touching points, doesn't count.");
  Assert(!PointCreationRules.ScriptAddsPoints("foreach (var n in nums) CivilDoc.CogoPoints.Remove(CivilDoc.CogoPoints.GetPointByPointNumber(n));\nvar order = new ObjectIdCollection(); order.Add(id);")
         && PointCreationRules.ScriptRemovesPoints("foreach (var n in nums) CivilDoc.CogoPoints.Remove(CivilDoc.CogoPoints.GetPointByPointNumber(n));"),
    "A script that deletes points and adds to another collection asks to delete, not to create.");
  Assert(PointCreationRules.ScriptAddsPoints("Helper(CivilDoc.CogoPoints);"),
    "Handing the point collection to other code asks, since what it does there can't be seen.");
  Assert(!PointCreationRules.ScriptRemovesPoints("foreach (ObjectId id in CivilDoc.CogoPoints) Count++;"),
    "Reading points doesn't count as deleting them.");
  Assert(PointCreationRules.IsCreateCommand("CREATEPOINTS") && PointCreationRules.IsCreateCommand("createpoints")
         && !PointCreationRules.IsCreateCommand("IMPORTPOINTS"),
    "CREATEPOINTS asks; IMPORTPOINTS (Civil 3D's own importer) doesn't.");

  Console.WriteLine("P1 disconnect cancellation, P2 filesystem/JSON-RPC, P4 bounded-job, UTF-8 request, point text, pipe arithmetic, station and point creation checks passed.");
}
finally
{
  Directory.Delete(testRoot, recursive: true);
}

static void ExpectCode(string expectedCode, Action action)
{
  try
  {
    action();
  }
  catch (JsonRpcDispatchException exception) when (exception.Code == expectedCode)
  {
    return;
  }

  throw new InvalidOperationException($"Expected JsonRpcDispatchException code {expectedCode}.");
}

static void Assert(bool condition, string message)
{
  if (!condition)
  {
    throw new InvalidOperationException(message);
  }
}

static int GetFreeTcpPort()
{
  var listener = new TcpListener(IPAddress.Loopback, 0);
  listener.Start();
  var port = ((IPEndPoint)listener.LocalEndpoint).Port;
  listener.Stop();
  return port;
}

/// <summary>The one global the loop guard needs, as ScriptContext gives it to a real script.</summary>
public sealed class ScriptGlobals(CancellationToken cancellationToken)
{
  public CancellationToken CancellationToken { get; } = cancellationToken;
}
