namespace MCModCoder.Core.Simulation;

public enum SimBlock { Air, Wire, Torch, Lever, Lamp, RepeaterEast, RepeaterWest, RepeaterSouth, RepeaterNorth, SolidBlock, Button }

/// <summary>红石平面网格模拟器（信号强度 15 级衰减）</summary>
public class RedstoneGrid
{
    public int W { get; }
    public int H { get; }
    public SimBlock[,] Type { get; }
    /// <summary>信号强度 0-15</summary>
    public int[,] Strength { get; private set; }
    /// <summary>持久状态：拉杆是否开启、灯是否点亮、按钮剩余时间</summary>
    public bool[,] OnState { get; private set; }
    public int[,] Timer { get; private set; }

    public RedstoneGrid(int w, int h)
    {
        W = Math.Clamp(w, 1, 256); H = Math.Clamp(h, 1, 256);   // 参数钳制，防御非法尺寸
        Type = new SimBlock[W, H];
        Strength = new int[W, H];
        OnState = new bool[W, H];
        Timer = new int[W, H];
    }

    public void Place(int x, int y, SimBlock t)
    {
        if (In(x, y)) { Type[x, y] = t; OnState[x, y] = t == SimBlock.Lever; Timer[x, y] = 0; }
    }

    public void Remove(int x, int y)
    {
        if (In(x, y)) { Type[x, y] = SimBlock.Air; OnState[x, y] = false; }
    }

    /// <summary>点击交互（拉杆切换 / 按钮触发）</summary>
    public bool Interact(int x, int y)
    {
        if (!In(x, y)) return false;
        switch (Type[x, y])
        {
            case SimBlock.Lever:
                OnState[x, y] = !OnState[x, y];
                return true;
            case SimBlock.Button:
                Timer[x, y] = 10; // 10 tick
                return true;
            default:
                return false;
        }
    }

    /// <summary>推进一步（1 刻）。返回是否有变化。</summary>
    public bool Tick()
    {
        var oldStr = (int[,])Strength.Clone();
        var oldLamp = (bool[,])OnState.Clone();

        // 1. 按钮计时
        for (int x = 0; x < W; x++)
            for (int y = 0; y < H; y++)
                if (Type[x, y] == SimBlock.Button && Timer[x, y] > 0)
                    Timer[x, y]--;

        // 2. 收集信号源
        var newStr = new int[W, H];
        for (int x = 0; x < W; x++)
            for (int y = 0; y < H; y++)
            {
                switch (Type[x, y])
                {
                    case SimBlock.Lever when OnState[x, y]:
                    case SimBlock.Button when Timer[x, y] > 0:
                        newStr[x, y] = 15;
                        break;
                }
            }

        // 3. 迭代传播（导线衰减 + 中继器隔离 + 火把反相）
        for (int iter = 0; iter < 32; iter++)
        {
            var cur = (int[,])newStr.Clone();
            bool changed = false;
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    switch (Type[x, y])
                    {
                        case SimBlock.Wire:
                        {
                            int m = MaxNeighbor(cur, x, y);
                            int v = Math.Max(0, m - 1);
                            if (v > cur[x, y]) { cur[x, y] = v; changed = true; }
                            break;
                        }
                        case SimBlock.Torch:
                        {
                            // 火把：被相邻信号点亮供能则熄灭，否则输出 15
                            bool input = MaxNeighbor(cur, x, y) > 0;
                            bool outOn = !input;
                            if (outOn && cur[x, y] < 15) { cur[x, y] = 15; changed = true; }
                            else if (!outOn && cur[x, y] > 0) { cur[x, y] = 0; changed = true; }
                            break;
                        }
                        case SimBlock.RepeaterEast or SimBlock.RepeaterWest or SimBlock.RepeaterSouth or SimBlock.RepeaterNorth:
                        {
                            var (inX, inY, outX, outY) = RepeaterDirs(Type[x, y]);
                            int inStr = In(x + inX, y + inY) ? cur[x + inX, y + inY] : 0;
                            // 简化：忽略延迟方向输出，满强度输出
                            if (inStr > 0 && cur[x, y] < 15) { cur[x, y] = 15; changed = true; }
                            else if (inStr == 0 && cur[x, y] > 0) { cur[x, y] = 0; changed = true; }
                            _ = (outX, outY);
                            break;
                        }
                    }
                }
            newStr = cur;
            if (!changed) break;
        }

        // 4. 灯与状态
        for (int x = 0; x < W; x++)
            for (int y = 0; y < H; y++)
                if (Type[x, y] == SimBlock.Lamp)
                    OnState[x, y] = MaxNeighbor(newStr, x, y) > 0;

        Strength = newStr;
        // 变化检测（含灯）
        bool diff = false;
        for (int x = 0; x < W && !diff; x++)
            for (int y = 0; y < H; y++)
                if (Strength[x, y] != oldStr[x, y] || (Type[x, y] == SimBlock.Lamp && OnState[x, y] != oldLamp[x, y]))
                { diff = true; break; }
        return diff;
    }

    private int MaxNeighbor(int[,] s, int x, int y)
    {
        int m = 0;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (!In(nx, ny)) continue;
                var t = Type[nx, ny];
                // 导线只从正交方向接受信号（简化：允许斜向，便于教学演示）
                if (t == SimBlock.Wire || t == SimBlock.Torch || t == SimBlock.Lever ||
                    t == SimBlock.Button || t == SimBlock.SolidBlock ||
                    t is SimBlock.RepeaterEast or SimBlock.RepeaterWest or SimBlock.RepeaterSouth or SimBlock.RepeaterNorth)
                    m = Math.Max(m, s[nx, ny]);
            }
        return m;
    }

    private (int inX, int inY, int outX, int outY) RepeaterDirs(SimBlock t) => t switch
    {
        SimBlock.RepeaterEast => (-1, 0, 1, 0),
        SimBlock.RepeaterWest => (1, 0, -1, 0),
        SimBlock.RepeaterSouth => (0, -1, 0, 1),
        SimBlock.RepeaterNorth => (0, 1, 0, -1),
        _ => (0, 0, 0, 0)
    };

    public bool In(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
}

/// <summary>实体行为模拟（游走 AI + 简单寻路回避）</summary>
public class EntitySimulator
{
    public class Entity
    {
        public double X, Y;
        public double DX, DY;
        public string Kind { get; set; } = "zombie";  // zombie / cow / sheep
        public int Age;
    }

    public List<Entity> Entities { get; } = new();
    private readonly Random _rng = new();

    public void Spawn(string kind, int count, int w, int h)
    {
        // 参数钳制：画布过小 / 数量异常时不再抛出 ArgumentOutOfRangeException
        w = Math.Max(3, w);
        h = Math.Max(3, h);
        count = Math.Clamp(count, 0, 500);
        for (int i = 0; i < count; i++)
        {
            double ang = _rng.NextDouble() * Math.PI * 2;
            Entities.Add(new Entity
            {
                X = _rng.Next(1, w - 1),
                Y = _rng.Next(1, h - 1),
                DX = Math.Cos(ang), DY = Math.Sin(ang),
                Kind = kind
            });
        }
    }

    /// <summary>步进：随机转向 + 边界反弹 + 同类聚集倾向</summary>
    public void Tick(int w, int h)
    {
        foreach (var e in Entities)
        {
            e.Age++;
            if (_rng.NextDouble() < 0.05)   // 随机转向
            {
                double ang = Math.Atan2(e.DY, e.DX) + (_rng.NextDouble() - 0.5) * 1.2;
                e.DX = Math.Cos(ang); e.DY = Math.Sin(ang);
            }
            e.X += e.DX * 0.15;
            e.Y += e.DY * 0.15;
            if (e.X < 0.5 || e.X > w - 0.5) { e.DX = -e.DX; e.X = Math.Clamp(e.X, 0.5, w - 0.5); }
            if (e.Y < 0.5 || e.Y > h - 0.5) { e.DY = -e.DY; e.Y = Math.Clamp(e.Y, 0.5, h - 0.5); }
        }
    }

    public void Clear() => Entities.Clear();
}
