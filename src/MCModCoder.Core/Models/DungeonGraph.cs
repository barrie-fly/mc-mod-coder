namespace MCModCoder.Core.Models;

/// <summary>地牢房间</summary>
public class DungeonRoom
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Kind { get; set; } = "normal";   // normal / entrance / treasure / spawner / boss / corridor-hub
    public string CnKind => Kind switch
    {
        "entrance" => "入口",
        "treasure" => "宝物房",
        "spawner" => "刷怪房",
        "boss" => "BOSS房",
        _ => "普通房间"
    };
    public int X { get; set; }      // 中心 X
    public int Y { get; set; }      // 中心 Y（平面坐标）
    public int W { get; set; } = 9;
    public int H { get; set; } = 9;
    public string Material { get; set; } = "minecraft:bricks";
}

/// <summary>地牢走廊</summary>
public class DungeonCorridor
{
    public string FromRoom { get; set; } = "";
    public string ToRoom { get; set; } = "";
    public string Style { get; set; } = "straight";  // straight / L-shaped
    public int Width { get; set; } = 3;
}

/// <summary>地牢生成规则</summary>
public class DungeonGenRules
{
    /// <summary>random（随机生成）/ fixed（固定结构，使用手动布局）</summary>
    public string Mode { get; set; } = "random";
    public int Seed { get; set; } = 12345;
    public int RoomCount { get; set; } = 10;
    public int MinRoomSize { get; set; } = 7;
    public int MaxRoomSize { get; set; } = 15;
    public int CorridorWidth { get; set; } = 3;
    public bool IncludeTreasure { get; set; } = true;
    public bool IncludeSpawner { get; set; } = true;
    public bool IncludeBoss { get; set; } = true;
    public double LoopConnectionRate { get; set; } = 0.15;   // 额外环路连接概率
    public string WallMaterial { get; set; } = "minecraft:bricks";
    public string FloorMaterial { get; set; } = "minecraft:stone_bricks";
}

/// <summary>地牢结构图（房间 + 走廊）</summary>
public class DungeonGraph
{
    public string Name { get; set; } = "NewDungeon";
    public string Description { get; set; } = "";
    public DungeonGenRules Rules { get; set; } = new();
    public List<DungeonRoom> Rooms { get; set; } = new();
    public List<DungeonCorridor> Corridors { get; set; } = new();

    public DungeonRoom? RoomById(string id) => Rooms.FirstOrDefault(r => r.Id == id);

    /// <summary>按规则随机生成地牢（随机模式）</summary>
    public void Generate()
    {
        var rng = new Random(Rules.Seed);
        Rooms.Clear();
        Corridors.Clear();
        if (Rules.Mode == "fixed") return;

        int n = Math.Max(2, Rules.RoomCount);
        int minSize = Math.Max(4, Rules.MinRoomSize);
        int maxSize = Math.Max(minSize, Rules.MaxRoomSize);
        // 1) 入口
        var entrance = new DungeonRoom { Kind = "entrance", X = 0, Y = 0, W = minSize, H = minSize };
        Rooms.Add(entrance);

        // 2) 随机游走布置房间（每次放置最多尝试 200 次，空间不足时提前结束，避免死循环）
        for (int i = 1; i < n; i++)
        {
            bool placed = false;
            for (int attempt = 0; attempt < 200 && !placed; attempt++)
            {
                var from = Rooms[rng.Next(Rooms.Count)];
                var dir = (DungeonDir)rng.Next(4);
                int w = rng.Next(minSize, maxSize + 1);
                int h = rng.Next(minSize, maxSize + 1);
                int dist = (from.W + from.H) / 2 + w / 2 + h / 2 + rng.Next(6, 18);
                var room = new DungeonRoom
                {
                    W = w, H = h,
                    X = from.X + (dir == DungeonDir.East ? dist : dir == DungeonDir.West ? -dist : rng.Next(-4, 5)),
                    Y = from.Y + (dir == DungeonDir.South ? dist : dir == DungeonDir.North ? -dist : rng.Next(-4, 5)),
                };
                if (Rooms.Any(r => Math.Abs(r.X - room.X) < (r.W + room.W) / 2 + 2 &&
                                   Math.Abs(r.Y - room.Y) < (r.H + room.H) / 2 + 2))
                    continue;
                Rooms.Add(room);
                Corridors.Add(new DungeonCorridor { FromRoom = from.Id, ToRoom = room.Id, Width = Rules.CorridorWidth });
                placed = true;
            }
            if (!placed) break;   // 已无空间可放，保留当前房间数
        }

        // 3) 特殊房间
        var normals = Rooms.Where(r => r.Kind == "normal").ToList();
        if (Rules.IncludeTreasure && normals.Count > 0)
            normals[rng.Next(normals.Count)].Kind = "treasure";
        if (Rules.IncludeSpawner)
        {
            var sp = normals.Where(r => r.Kind == "normal").Take(2);
            foreach (var r in sp) r.Kind = "spawner";
        }
        if (Rules.IncludeBoss && normals.Count > 2)
        {
            // 距入口最远的作为 BOSS 房
            var far = normals.Where(r => r.Kind == "normal")
                .OrderByDescending(r => Math.Abs(r.X) + Math.Abs(r.Y)).First();
            far.Kind = "boss";
            far.W = Math.Max(far.W, 15); far.H = Math.Max(far.H, 15);
        }

        // 4) 环路连接
        var linkable = Rooms.ToList();
        for (int i = 0; i < linkable.Count; i++)
            for (int j = i + 1; j < linkable.Count; j++)
                if (rng.NextDouble() < Rules.LoopConnectionRate)
                    Corridors.Add(new DungeonCorridor { FromRoom = linkable[i].Id, ToRoom = linkable[j].Id, Width = Rules.CorridorWidth });
    }

    private enum DungeonDir { North, South, East, West }
}
