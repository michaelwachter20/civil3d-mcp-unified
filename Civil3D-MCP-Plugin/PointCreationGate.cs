// The rule that COGO points are made only when the user asks, and the window that lets the user say so.
// Points from a file come in through Civil 3D's own importer (importPointFile) with no question. Every other way the
// AI can make points first calls RequireAsync, which opens a modal "Allow / Don't allow" window in Civil 3D:
// PointCommands.createCogoPoints and the typed-in importCogoPoints (civil3d_point create / import, pointDomain.ts),
// ScriptCommands for civil3d_execute and saved scripts whose code adds or deletes points (PointCreationRules decides)
// and for acad_command with a point-making command (CREATEPOINTS ...). "Don't allow" is the default button, and no
// answer within 50 s counts as no; then the call fails with CIVIL3D.USER_DECLINED and nothing is changed.
// This is on top of the TS approval token: the AI can request a token itself, but only the user can click Allow.
// CIVIL3D_POINT_CREATION=ask (default) | off (always refuse) | allow (no window; unattended tests only).
// The window is shown on the main thread (CivilExecution.OnMainThreadAsync); no drawing access here.
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// The user's say over points made by the AI. Points from a file go through Civil 3D's own importer
/// (<c>importPointFile</c>, CogoPoints.ImportPoints), which needs no question. Any other way of making COGO points
/// (the <c>createCogoPoints</c> and text <c>importCogoPoints</c> actions, a script that calls CogoPoints.Add, the
/// CREATEPOINTS commands) first opens a window in Civil 3D asking the user, which the AI can't answer for them (unlike
/// the approval token, which it requests itself). Unanswered for <see cref="TimeoutSeconds"/>, it counts as no, so
/// the refusal gets back before an MCP client gives up on the call (60 s is a common limit, and a race with it ends
/// in a bare "Request timed out") and nothing is made later behind its back.
/// <para>
/// CIVIL3D_POINT_CREATION sets the policy: <c>ask</c> (the default), <c>off</c> (always refused, no window) or
/// <c>allow</c> (no window: for unattended test setups only).
/// </para>
/// </summary>
public static class PointCreationGate
{
  /// <summary>Seconds the window waits before counting as "Don't allow"; below the common 60 s client limit.</summary>
  public const int TimeoutSeconds = 50;

  /// <summary>True when <paramref name="code"/> (a script) makes COGO points (PointCreationRules).</summary>
  public static bool ScriptAddsPoints(string code) => PointCreationRules.ScriptAddsPoints(code);

  /// <summary>True when <paramref name="code"/> (a script) deletes COGO points (PointCreationRules).</summary>
  public static bool ScriptRemovesPoints(string code) => PointCreationRules.ScriptRemovesPoints(code);

  /// <summary>What a script does to points, for the window's wording: "create", "delete", "create and delete", or
  /// null when it does neither.</summary>
  public static string? ScriptPointAction(string code)
  {
    var adds = ScriptAddsPoints(code);
    var removes = ScriptRemovesPoints(code);
    return adds && removes ? "create and delete" : adds ? "create" : removes ? "delete" : null;
  }

  /// <summary>True for one of Civil 3D's point-making commands (PointCreationRules).</summary>
  public static bool IsCreateCommand(string commandName) => PointCreationRules.IsCreateCommand(commandName);

  /// <summary>The details for code the AI runs: what it says it does, the values it passes and the code itself.</summary>
  public static string ScriptDetails(string heading, string? description, JsonObject? args, string code)
  {
    var lines = new List<string> { heading };
    if (!string.IsNullOrWhiteSpace(description)) lines.Add(description);
    lines.Add("");
    lines.Add("Run with:");
    if (args == null || args.Count == 0) lines.Add("  (no values)");
    else lines.AddRange(args.Select(pair => $"  {pair.Key} = {pair.Value?.ToJsonString() ?? "null"}"));
    lines.Add("");
    lines.Add("Code:");
    lines.Add(code);
    return string.Join("\n", lines);
  }

  /// <summary>The details for points the AI types in: one line each, the first <paramref name="max"/> of them.</summary>
  public static string PointListDetails(IReadOnlyList<string> pointLines, int max = 25)
  {
    var shown = pointLines.Take(max).ToList();
    if (pointLines.Count > max) shown.Add($"...and {pointLines.Count - max} more");
    return "Points:\n" + string.Join("\n", shown);
  }

  /// <summary>
  /// Asks the user in Civil 3D whether the AI may make points (<paramref name="what"/> says what, e.g. "3 COGO points,
  /// the first at E 2000.50, N 5000.25"). <paramref name="details"/>, when given, goes in a collapsed "Details" section
  /// under it (the values the AI passed, the code it runs, the points it types in), so the user can look before
  /// answering. <paramref name="action"/> ("create", "delete", "create and delete") goes in the window's title and
  /// question. Returns when allowed (or at once when the policy is allow); throws CIVIL3D.USER_DECLINED when the user
  /// says no or doesn't answer in time, and CIVIL3D.POINT_CREATION_OFF when the policy is off. Callers ask before they
  /// queue their write, so nothing has been changed when it throws.
  /// </summary>
  public static async Task RequireAsync(string what, string? details = null, string action = "create")
  {
    var policy = (Environment.GetEnvironmentVariable("CIVIL3D_POINT_CREATION") ?? "ask").Trim().ToLowerInvariant();
    if (policy == "allow")
    {
      return;
    }

    if (policy == "off")
    {
      throw new JsonRpcDispatchException("CIVIL3D.POINT_CREATION_OFF",
        $"Making points this way is turned off (CIVIL3D_POINT_CREATION=off): {what.TrimEnd('.')}. Bring points in from a file with " +
        "civil3d_point action import_file (Civil 3D's own importer).");
    }

    var allowed = await CivilExecution.OnMainThreadAsync(() => Task.FromResult(Ask(what, details, action)));
    if (!allowed)
    {
      throw new JsonRpcDispatchException("CIVIL3D.USER_DECLINED",
        $"The user didn't allow it in Civil 3D (said no, or didn't answer within {TimeoutSeconds} s): {what.TrimEnd('.')}. Nothing " +
        "was changed. For points from a file use civil3d_point action import_file (Civil 3D's own importer).");
    }
  }

  /// <summary>The question window, modal in Civil 3D: "Allow" / "Don't allow" (the default), closing as no after
  /// TimeoutSeconds. True only for an explicit "Allow". The drawing the points would go into and any details sit in a
  /// collapsed "Details" section, so the short summary stays the first thing read.</summary>
  private static bool Ask(string what, string? details, string action)
  {
    var remaining = TimeoutSeconds;
    var countdown = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7 };
    var allow = new Button { Content = "Allow", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
    var deny = new Button { Content = "Don't allow", MinWidth = 90, IsDefault = true, IsCancel = true };
    var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
      Margin = new Thickness(0, 14, 0, 0) };
    buttons.Children.Add(allow);
    buttons.Children.Add(deny);
    var body = new StackPanel { Margin = new Thickness(16) };
    body.Children.Add(new TextBlock
    {
      Text = $"The AI (Civil 3D MCP) wants to {action} points in this drawing:",
      FontWeight = FontWeights.SemiBold,
      TextWrapping = TextWrapping.Wrap,
    });
    body.Children.Add(new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Margin = new Thickness(0, 6, 0, 0) });
    var drawing = App.DocumentManager.MdiActiveDocument?.Name;
    var detailText = (string.IsNullOrWhiteSpace(drawing) ? "" : $"Into drawing: {System.IO.Path.GetFileName(drawing)}\n\n")
      + (string.IsNullOrWhiteSpace(details) ? "No more detail was given." : details);
    body.Children.Add(new Expander
    {
      Header = "Details: what the AI is doing",
      IsExpanded = false,
      Margin = new Thickness(0, 8, 0, 0),
      MaxWidth = 560,
      Content = new ScrollViewer
      {
        MaxHeight = 320,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new TextBox
        {
          Text = detailText,
          IsReadOnly = true,
          BorderThickness = new Thickness(0),
          FontFamily = new System.Windows.Media.FontFamily("Consolas"),
          TextWrapping = TextWrapping.NoWrap,
          Margin = new Thickness(0, 4, 0, 0),
        },
      },
    });
    body.Children.Add(countdown);
    body.Children.Add(buttons);
    var window = new Window
    {
      Title = $"Civil 3D MCP: {action} points?",
      Content = body,
      SizeToContent = SizeToContent.WidthAndHeight,
      ResizeMode = ResizeMode.NoResize,
      WindowStartupLocation = WindowStartupLocation.CenterScreen,
      Topmost = true,
      ShowInTaskbar = false,
    };

    void Tick()
    {
      countdown.Text = $"Counts as \"Don't allow\" in {remaining} s.";
    }

    Tick();
    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    timer.Tick += (_, _) =>
    {
      remaining--;
      Tick();
      if (remaining <= 0)
      {
        timer.Stop();
        window.DialogResult = false;
      }
    };
    allow.Click += (_, _) => window.DialogResult = true;
    window.Closed += (_, _) => timer.Stop();
    timer.Start();
    PluginLog.Info("PointCreationGate", $"Asking the user: {what}");
    var answer = App.ShowModalWindow(window) == true;
    PluginLog.Info("PointCreationGate", answer ? "Allowed by the user." : "Not allowed (no, or no answer).");
    return answer;
  }
}
