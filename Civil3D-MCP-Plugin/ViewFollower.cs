using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Keeps MCP work on screen: after a committed write, pans (and zooms out only
/// if needed) so the created or modified model-space entities are visible.
/// Never zooms in and never fails the operation that triggered it.
/// Toggle with C3DMCPFOLLOW or CIVIL3D_FOLLOW_VIEW=false.
/// </summary>
internal static class ViewFollower
{
  private const int MaxTrackedObjects = 20000;
  private const double MarginFraction = 0.15;

  public static bool Enabled { get; set; } =
    !string.Equals(Environment.GetEnvironmentVariable("CIVIL3D_FOLLOW_VIEW"), "false", StringComparison.OrdinalIgnoreCase);

  /// <summary>Starts recording appended/modified objects; dispose once the transaction has ended.</summary>
  public static ChangeTracker Track(Database database) => new(database, Enabled);

  public static void Follow(Document document, ChangeTracker tracker)
  {
    if (!Enabled || tracker.Ids.Count == 0)
    {
      return;
    }

    try
    {
      var database = document.Database;
      if (!database.TileMode)
      {
        return; // A layout tab is active; leave paper space views alone.
      }

      var extents = GetModelSpaceExtents(database, tracker.Ids);
      if (extents == null)
      {
        return;
      }

      MoveViewToShow(document, extents.Value);
    }
    catch (System.Exception exception)
    {
      PluginLog.Warn("ViewFollower", $"Could not move the view to the changed objects: {exception.Message}");
    }
  }

  private static Extents3d? GetModelSpaceExtents(Database database, IReadOnlyCollection<ObjectId> ids)
  {
    var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(database);
    Extents3d? extents = null;
    using var transaction = database.TransactionManager.StartOpenCloseTransaction();
    foreach (var id in ids)
    {
      if (id.IsNull || id.IsErased || !id.IsValid || id.Database != database)
      {
        continue;
      }

      if (transaction.GetObject(id, OpenMode.ForRead, false) is not Entity entity || entity.BlockId != modelSpaceId)
      {
        continue;
      }

      var bounds = entity.Bounds;
      if (bounds == null)
      {
        continue;
      }

      if (extents == null)
      {
        extents = bounds;
      }
      else
      {
        var combined = extents.Value;
        combined.AddExtents(bounds.Value);
        extents = combined;
      }
    }

    transaction.Commit();
    return extents;
  }

  private static void MoveViewToShow(Document document, Extents3d extents)
  {
    var editor = document.Editor;
    using var view = editor.GetCurrentView();

    // World -> display coordinates for the current view (handles UCS views and twist).
    var displayToWorld =
      Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
      Matrix3d.Displacement(view.Target - Point3d.Origin) *
      Matrix3d.PlaneToWorld(view.ViewDirection);
    var worldToDisplay = displayToWorld.Inverse();

    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
    var min = extents.MinPoint;
    var max = extents.MaxPoint;
    foreach (var x in new[] { min.X, max.X })
    foreach (var y in new[] { min.Y, max.Y })
    foreach (var z in new[] { min.Z, max.Z })
    {
      var point = new Point3d(x, y, z).TransformBy(worldToDisplay);
      minX = Math.Min(minX, point.X);
      minY = Math.Min(minY, point.Y);
      maxX = Math.Max(maxX, point.X);
      maxY = Math.Max(maxY, point.Y);
    }

    var center = view.CenterPoint;
    var halfWidth = view.Width / 2;
    var halfHeight = view.Height / 2;
    if (minX >= center.X - halfWidth && maxX <= center.X + halfWidth &&
        minY >= center.Y - halfHeight && maxY <= center.Y + halfHeight)
    {
      return; // Already fully visible.
    }

    // Keep the current zoom when the change fits; otherwise zoom out just enough.
    var aspect = view.Width / view.Height;
    var neededWidth = (maxX - minX) * (1 + 2 * MarginFraction);
    var neededHeight = (maxY - minY) * (1 + 2 * MarginFraction);
    var width = Math.Max(view.Width, Math.Max(neededWidth, neededHeight * aspect));

    view.CenterPoint = new Point2d((minX + maxX) / 2, (minY + maxY) / 2);
    view.Width = width;
    view.Height = width / aspect;
    editor.SetCurrentView(view);
  }

  internal sealed class ChangeTracker : IDisposable
  {
    private readonly Database _database;
    private readonly HashSet<ObjectId> _ids = new();
    private bool _subscribed;

    internal ChangeTracker(Database database, bool enabled)
    {
      _database = database;
      if (enabled)
      {
        _database.ObjectAppended += OnObjectChanged;
        _database.ObjectModified += OnObjectChanged;
        _subscribed = true;
      }
    }

    public IReadOnlyCollection<ObjectId> Ids => _ids;

    private void OnObjectChanged(object sender, ObjectEventArgs e)
    {
      if (_ids.Count < MaxTrackedObjects)
      {
        _ids.Add(e.DBObject.ObjectId);
      }
    }

    public void Dispose()
    {
      if (_subscribed)
      {
        _database.ObjectAppended -= OnObjectChanged;
        _database.ObjectModified -= OnObjectChanged;
        _subscribed = false;
      }
    }
  }
}
