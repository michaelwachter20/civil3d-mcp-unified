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
/// the approval token, which it requests itself). Unanswered for <see cref="TimeoutSeconds"/>, it counts as no, so a
/// request never waits past the server's command timeout (120 s) and nothing is made later behind its back.
/// <para>
/// CIVIL3D_POINT_CREATION sets the policy: <c>ask</c> (the default), <c>off</c> (always refused, no window) or
/// <c>allow</c> (no window: for unattended test setups only).
/// </para>
/// </summary>
public static class PointCreationGate
{
  public const int TimeoutSeconds = 60;

  /// <summary>True when <paramref name="code"/> (a script) makes COGO points (PointCreationRules).</summary>
  public static bool ScriptAddsPoints(string code) => PointCreationRules.ScriptAddsPoints(code);

  /// <summary>True for one of Civil 3D's point-making commands (PointCreationRules).</summary>
  public static bool IsCreateCommand(string commandName) => PointCreationRules.IsCreateCommand(commandName);

  /// <summary>
  /// Asks the user in Civil 3D whether the AI may make points (<paramref name="what"/> says what, e.g. "3 COGO points,
  /// the first at E 2000.50, N 5000.25"). Returns when allowed; throws CIVIL3D.USER_DECLINED when the user says no or
  /// doesn't answer in time, and CIVIL3D.POINT_CREATION_OFF when the policy is off.
  /// </summary>
  public static async Task RequireAsync(string what)
  {
    var policy = (Environment.GetEnvironmentVariable("CIVIL3D_POINT_CREATION") ?? "ask").Trim().ToLowerInvariant();
    if (policy == "allow")
    {
      return;
    }

    if (policy == "off")
    {
      throw new JsonRpcDispatchException("CIVIL3D.POINT_CREATION_OFF",
        $"Making points this way is turned off (CIVIL3D_POINT_CREATION=off): {what}. Bring points in from a file with " +
        "civil3d_point action import_file (Civil 3D's own importer).");
    }

    var allowed = await CivilExecution.OnMainThreadAsync(() => Task.FromResult(Ask(what)));
    if (!allowed)
    {
      throw new JsonRpcDispatchException("CIVIL3D.USER_DECLINED",
        $"The user didn't allow it in Civil 3D (said no, or didn't answer within {TimeoutSeconds} s): {what}. Nothing " +
        "was created. For points from a file use civil3d_point action import_file (Civil 3D's own importer).");
    }
  }

  /// <summary>The question window, modal in Civil 3D: "Allow" / "Don't allow" (the default), closing as no after
  /// TimeoutSeconds. True only for an explicit "Allow".</summary>
  private static bool Ask(string what)
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
      Text = "The AI (Civil 3D MCP) wants to create points in this drawing:",
      FontWeight = FontWeights.SemiBold,
      TextWrapping = TextWrapping.Wrap,
    });
    body.Children.Add(new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Margin = new Thickness(0, 6, 0, 0) });
    body.Children.Add(countdown);
    body.Children.Add(buttons);
    var window = new Window
    {
      Title = "Civil 3D MCP: create points?",
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
