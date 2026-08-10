using Orbit_App.ViewModels;

namespace Orbit_App.Services;

/// <summary>
/// Phone-home-screen style packing: cells flow left-to-right, wrap to next row,
/// and reflow when one is dragged/resized so others move out of the way.
/// Limbo is pinned bottom-right, away from the packed project band.
/// </summary>
public static class WorkbenchPacker
{
    public const double Gap = 16;

    public static double BoardWidth(double scrollerWidth) =>
        Math.Max(320, scrollerWidth > 100 ? scrollerWidth - 24 : 960);

    public static void EnsureSizes(IEnumerable<ProjectCellVm> cells, double defaultW, double defaultH)
    {
        foreach (var cell in cells)
        {
            if (cell.BoardW <= 0)
            {
                cell.BoardW = defaultW;
            }

            if (cell.BoardH <= 0)
            {
                cell.BoardH = defaultH;
            }
        }
    }

    /// <summary>Pack cells in current list order into rows within boardWidth.</summary>
    public static void Pack(IList<ProjectCellVm> ordered, double boardWidth)
    {
        double x = 0;
        double y = 0;
        double rowHeight = 0;
        var width = Math.Max(boardWidth, 200);

        for (var i = 0; i < ordered.Count; i++)
        {
            var cell = ordered[i];
            var w = Math.Max(200, cell.BoardW);
            var h = Math.Max(180, cell.BoardH);
            cell.BoardW = w;
            cell.BoardH = h;

            if (x > 0 && x + w > width)
            {
                x = 0;
                y += rowHeight + Gap;
                rowHeight = 0;
            }

            cell.BoardX = x;
            cell.BoardY = y;
            cell.SortOrder = i;
            cell.HasSavedLayout = true;
            x += w + Gap;
            rowHeight = Math.Max(rowHeight, h);
        }
    }

    /// <summary>
    /// Pack project/task cells, then pin Limbo to the bottom-right of the board
    /// (below the project band when needed so it stays out of the way).
    /// </summary>
    public static List<ProjectCellVm> PackProjectsAndPinLimbo(
        IEnumerable<ProjectCellVm> cells,
        double boardWidth,
        double viewportHeight)
    {
        var all = cells.ToList();
        var limbo = all.FirstOrDefault(c => c.IsLimboCell);
        var projects = OrderForPack(all.Where(c => !c.IsLimboCell)).ToList();
        Pack(projects, boardWidth);
        if (limbo is not null)
        {
            PinLimboBottomRight(limbo, projects, boardWidth, viewportHeight);
            projects.Add(limbo);
        }

        return projects;
    }

    public static void PinLimboBottomRight(
        ProjectCellVm limbo,
        IReadOnlyList<ProjectCellVm> packedProjects,
        double boardWidth,
        double viewportHeight)
    {
        var w = Math.Max(200, limbo.BoardW > 0 ? limbo.BoardW : 280);
        var h = Math.Max(180, limbo.BoardH > 0 ? limbo.BoardH : 240);
        limbo.BoardW = w;
        limbo.BoardH = h;

        var packedBottom = packedProjects.Count == 0
            ? 0
            : packedProjects.Max(c => c.BoardY + c.BoardH);
        var viewport = Math.Max(viewportHeight > 100 ? viewportHeight - 16 : 600, packedBottom + Gap + h);

        limbo.BoardX = Math.Max(0, Math.Max(boardWidth, 320) - w);
        var preferredY = Math.Max(0, viewport - h);
        limbo.BoardY = preferredY < packedBottom + Gap
            ? packedBottom + Gap
            : preferredY;
        limbo.SortOrder = 10_000;
        limbo.HasSavedLayout = true;
    }

    public static List<ProjectCellVm> OrderForPack(IEnumerable<ProjectCellVm> cells) =>
        cells
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.BoardY)
            .ThenBy(c => c.BoardX)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Insert index from pointer among already-packed "others".</summary>
    public static int InsertIndexAt(
        IReadOnlyList<ProjectCellVm> othersPacked,
        double pointerX,
        double pointerY)
    {
        if (othersPacked.Count == 0)
        {
            return 0;
        }

        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < othersPacked.Count; i++)
        {
            var c = othersPacked[i];
            var cx = c.BoardX + (c.BoardW / 2);
            var cy = c.BoardY + (c.BoardH / 2);
            var dx = pointerX - cx;
            var dy = pointerY - cy;
            var d = (dx * dx) + (dy * dy);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        var target = othersPacked[best];
        var midX = target.BoardX + (target.BoardW / 2);
        var midY = target.BoardY + (target.BoardH / 2);
        var after = pointerY > midY + (target.BoardH * 0.2)
            || (Math.Abs(pointerY - midY) <= target.BoardH / 2 && pointerX >= midX);
        return after ? best + 1 : best;
    }

    public static List<ProjectCellVm> ReorderWithDrag(
        IReadOnlyList<ProjectCellVm> all,
        ProjectCellVm dragged,
        double pointerX,
        double pointerY,
        double boardWidth,
        double viewportHeight)
    {
        if (dragged.IsLimboCell)
        {
            var projects = all.Where(c => !c.IsLimboCell).ToList();
            Pack(projects, boardWidth);
            dragged.BoardX = Math.Max(0, pointerX - (dragged.BoardW / 2));
            dragged.BoardY = Math.Max(0, pointerY - (dragged.BoardH / 2));
            dragged.SortOrder = 10_000;
            var result = projects.ToList();
            result.Add(dragged);
            return result;
        }

        var limbo = all.FirstOrDefault(c => c.IsLimboCell);
        var others = all.Where(c => !ReferenceEquals(c, dragged) && !c.IsLimboCell).ToList();
        Pack(others, boardWidth);
        var insertAt = Math.Clamp(InsertIndexAt(others, pointerX, pointerY), 0, others.Count);
        others.Insert(insertAt, dragged);
        Pack(others, boardWidth);
        if (limbo is not null)
        {
            PinLimboBottomRight(limbo, others, boardWidth, viewportHeight);
            others.Add(limbo);
        }

        return others;
    }
}
