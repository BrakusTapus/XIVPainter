﻿using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.IoC;
using Dalamud.Logging;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using XIVPainter.Element2D;
using XIVPainter.Element3D;

namespace XIVPainter;

public class XIVPainter
{
    readonly string _name;

    readonly object _drawing2DLock = new object();
    IDrawing2D[] _drawing2DElements;

    readonly object _drawing3DLock = new object();
    List<IDrawing3D> _drawing3DElements = new List<IDrawing3D>();

    [PluginService]
    internal static DalamudPluginInterface _pluginInterface { get; set; }

    [PluginService]
    internal static Framework _framework { get; set; }

    [PluginService]
    internal static ClientState _clientState { get; set; }

    #region Config
    public bool UseTaskForAccelerate { get; set; } = true;

    public bool RemovePtsNotOnGround { get; set; } = true;
    public float DrawingHeight { get; set; } = 3;
    public float SampleLength { get; set; } = 0.5f;
    public float TimeToDisappear { get; set; } = 1.5f;
    public EaseFuncType DisappearType { get; set; } = EaseFuncType.Back;

    public byte DefaultWarningTime { get; set; } = 3;
    public float WarningRatio { get; set; } = 0.8f;
    public EaseFuncType WarningType { get; set; } = EaseFuncType.Cubic;

    #endregion

    /// <summary>
    /// Do not use it! Please use DalamudPluginInterface.Create<XIVPainter>(string name) to create this.
    /// </summary>
    /// <param name="name"></param>
    public XIVPainter(string name)
    {
        _name = name;

        _pluginInterface.UiBuilder.Draw += Draw;
        _framework.Update += Update;
    }

    public void Dispose()
    {
        _pluginInterface.UiBuilder.Draw -= Draw;
        _framework.Update -= Update;
    }

    private void Draw()
    {
        try
        {
            ImGuiHelpers.ForceNextWindowMainViewport();
            ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero);
            ImGui.SetNextWindowSize(ImGuiHelpers.MainViewport.Size);

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            if (ImGui.Begin(_name, ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysUseWindowPadding))
            {
                try
                {
                    lock(_drawing2DLock)
                    {
                        if (_drawing2DElements != null)
                        {
                            foreach (var element in _drawing2DElements)
                            {
                                element.Draw();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"{_name} failed to draw on Screen.");
                }
                ImGui.End();
            }

            ImGui.PopStyleVar();
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, $"{_name} failed to draw.");
        }
    }

    private void Update(Framework framework)
    {
        if (UseTaskForAccelerate)
        {
            Task.Run(UpdateData);
        }
        else
        {
            UpdateData();
        }
    }

    bool _started = false;
    private void UpdateData()
    {
        if (_started) return;
        _started = true;

        try
        {
            IDrawing2D[] elements = Array.Empty<IDrawing2D>();
            IEnumerable<IDrawing2D> relay = elements;
            lock (_drawing3DLock)
            {
                var remove = new List<IDrawing3D>(_drawing3DElements.Count);
                foreach (var ele in _drawing3DElements)
                {
                    if (ele.DeadTime != DateTime.MinValue)
                    {
                        var time = (DateTime.Now - ele.DeadTime).TotalSeconds;
                        if (time > TimeToDisappear) //Remove
                        {
                            remove.Add(ele);
                        }
                    }
                    ele.UpdateOnFrame();
                    relay = relay.Union(ele.To2D(this));
                }
                foreach (var r in remove)
                {
                    _drawing3DElements.Remove(r);
                }
            }

            elements = relay.OrderBy(drawing =>
            {
                if (drawing is PolylineDrawing poly)
                {
                    return poly._thickness == 0 ? 0 : 1;
                }
                else
                {
                    return 2;
                }
            }).ToArray();

            lock (_drawing2DLock)
            {
                _drawing2DElements = elements;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Something wrong with drawing");
        }

        _started = false;
    }

    #region Add Remove
    public void AddDrawing(IDrawing3D drawing)
    {
        drawing.DisappearType = DisappearType;
        drawing.TimeToDisappear = TimeToDisappear;
        drawing.WarningRatio = WarningRatio;
        drawing.WarningType = WarningType;

        lock (_drawing3DLock)
        {
            _drawing3DElements.Add(drawing);
        }
    }

    public void RemoveDrawing(IDrawing3D drawing)
    {
        if(drawing.DeadTime == DateTime.MinValue)
        {
            drawing.DeadTime = DateTime.Now;
        }
    }
    #endregion

    #region Trasform
    public unsafe Vector2[] GetPtsOnScreen(IEnumerable<Vector3> pts, bool isClosed)
    {
        //var camera = (Vector3)CameraManager.Instance()->CurrentCamera->Object.Position;
        var cameraPts = ProjectPtsOnGround(DivideCurve(pts, SampleLength, isClosed), DrawingHeight)
            //.Where(p =>
            //{
            //    var vec = p - camera;
            //    var dis = vec.Length() - 0.1f;
            //    return !BGCollisionModule.Raycast(camera, vec, out _, dis);
            //})
            .Select(WorldToCamera).ToArray();
        var changedPts = ChangePtsBehindCamera(cameraPts);

        return changedPts.Select(CameraToScreen).ToArray();
    }

    IEnumerable<Vector3> DivideCurve(IEnumerable<Vector3> worldPts, float length, bool isClosed)
    {
        if(worldPts.Count() < 2 || length <= 0.01f) return worldPts;

        IEnumerable<Vector3> pts = Array.Empty<Vector3>();
        bool isFirst = true;
        Vector3 lastPt = default;
        foreach (var pt in worldPts)
        {
            if (isFirst)
            {
                isFirst = false;
                lastPt = pt;
                continue;
            }
            pts = pts.Union(DashPoints(lastPt, pt, length));

            lastPt = pt;
        }

        if (isClosed)
        {
            pts = pts.Union(DashPoints(lastPt, worldPts.First(), length));
        }
        else
        {
            pts = pts.Append(lastPt);
        }
        return pts;
    }

    Vector3[] DashPoints(Vector3 previous, Vector3 next, float length)
    {
        var dir = next - previous;
        var count = Math.Max(1, (int)(dir.Length() / length));
        var points = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = previous + dir * i / count;
        }
        return points;
    }

    IEnumerable<Vector3> ChangePtsBehindCamera(Vector3[] cameraPts)
    {
        var changedPts = new List<Vector3>(cameraPts.Length * 2);

        for (int i = 0; i < cameraPts.Length; i++)
        {
            var pt1 = cameraPts[(i - 1 + cameraPts.Length) % cameraPts.Length];
            var pt2 = cameraPts[i];

            if (pt1.Z > 0 && pt2.Z <= 0)
            {
                GetPointOnPlane(pt1, ref pt2);
            }
            if (pt2.Z > 0 && pt1.Z <= 0)
            {
                GetPointOnPlane(pt2, ref pt1);
            }

            if (changedPts.Count > 0 && Vector3.Distance(pt1, changedPts[changedPts.Count - 1]) > 0.001f)
            {
                changedPts.Add(pt1);
            }

            changedPts.Add(pt2);
        }

        return changedPts.Where(p => p.Z > 0);
    }

    IEnumerable<Vector3> ProjectPtsOnGround(IEnumerable<Vector3> pts, float height) => pts.Select(pt =>
    {
        Vector3? result;
        var pUp = pt + Vector3.UnitY * height;
        if (BGCollisionModule.Raycast(pt + Vector3.UnitY * 10, -Vector3.UnitY, out var hit, 20))
        {
            var p = hit.Point;
            p.Y = Math.Max(p.Y, pt.Y - height);
            p.Y = Math.Min(p.Y, pt.Y + height);
            result = p;
        }
        else
        {
            result = RemovePtsNotOnGround ? null : pt - Vector3.UnitY * height;
        }
        return result;
    }).Where(pt => pt.HasValue).Select(pt => pt.Value);

    const float PLANE_Z = 0.001f;
    void GetPointOnPlane(Vector3 front, ref Vector3 back)
    {
        if (front.Z < 0) return;
        if (back.Z > 0) return;

        var ratio = (PLANE_Z - back.Z) / (front.Z - back.Z);
        back.X = (front.X - back.X) * ratio + back.X;
        back.Y = (front.Y - back.Y) * ratio + back.Y;
        back.Z = PLANE_Z;
    }

    unsafe Vector3 WorldToCamera(Vector3 worldPos)
    {
        var camera = CameraManager.Instance()->CurrentCamera;
        return Vector3.Transform(worldPos, camera->ViewMatrix * camera->RenderCamera->ProjectionMatrix);
    }

    unsafe Vector2 CameraToScreen(Vector3 cameraPos)
    {
        var screenPos = new Vector2(cameraPos.X / MathF.Abs(cameraPos.Z), cameraPos.Y / MathF.Abs(cameraPos.Z));
        var windowPos = ImGuiHelpers.MainViewport.Pos;

        var device = Device.Instance();
        float width = device->Width;
        float height = device->Height;

        screenPos.X = (0.5f * width * (screenPos.X + 1f)) + windowPos.X;
        screenPos.Y = (0.5f * height * (1f - screenPos.Y)) + windowPos.Y;

        return screenPos;
    }
    #endregion
}