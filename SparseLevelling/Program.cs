using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SparseLevelling
{
    /// <summary>
    /// Post-processing script to use auto bed levelling only on the actually used areas of the print bed
    /// 
    /// Assumptions:
    /// - PrusaSlicer
    /// - Relative Extrusion
    /// - Marlin
    /// - UBL(unified bed levelling is used)
    /// - bed origin is in the front left and has coordinates(0,0)
    /// - start gcode ends with "; End Start G-code" or whatever is set below
    /// - end gcode begins with "; Begin End G-code" or whatever is set below
    /// - there is a pair of lines containing "; BEGIN LEVEL" and "; END LEVEL". These lines and anything in between is replaced by the output of this script.
    /// </summary>
    class Program
    {
        const bool debug = true;

        string FILENAME = null;

        const string MAIN_START_MARKER = "; End Start G-code"; // ignore anything before this
        const string MAIN_END_MARKER = "; Begin End G-code"; // ignore anything after this

        const string BL_START_MARKER = "; BEGIN LEVEL";
        const string BL_END_MARKER = "; END LEVEL";

        const float MAX_HEIGHT = 1.0f; // this should be larger than the first layer height plus any offsets

        const int BEDSIZE_X = 310; // bed size mm
        const int BEDSIZE_Y = 310; // bed size mm
        const float INSET_FRONT = 5.0f; // mesh inset mm
        const float INSET_BACK = 5.0f;  // mesh inset mm
        const float INSET_LEFT = 5.0f;  // mesh inset mm
        const float INSET_RIGHT = 5.0f; // mesh inset mm
        const int POINTS_X = 7; // mesh points each direction
        const int POINTS_Y = 7; // mesh points each direction

        const float POINT_DX = (BEDSIZE_X - INSET_LEFT - INSET_RIGHT) / (POINTS_X - 1); // point distance
        const float POINT_DY = (BEDSIZE_Y - INSET_FRONT - INSET_BACK) / (POINTS_Y - 1);

        readonly NumberStyles numberStyle = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingWhite;
        readonly NumberFormatInfo formatInfo = NumberFormatInfo.InvariantInfo;
        readonly StringBuilder _Log = new StringBuilder();

        int MAIN_START_MARKER_I = -1;
        int MAIN_END_MARKER_I = -1;
        int BL_START_MARKER_I = -1;
        int BL_END_MARKER_I = -1;


        static void Main(string[] args) => new Program().Process(args);


        void Process(string[] args)
        {
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

                var fileContents = GetFileContents(args);
                InitAndSanityCheck(fileContents);
                var moves = GetRelevantMoves(fileContents);

                var grid = GetLevellingGridPoints();
                var usedAreas = GetUsedAreas(moves);

                var usedPoints = GetUsedLevellingPoints(usedAreas);
                var gcode = GenerateLevellingGcode(grid, usedPoints);

                OutputDebugData(grid, usedAreas, usedPoints, moves);
                WriteOutput(fileContents, gcode);
                WriteLog();
            }
            catch (Exception e)
            {
                Log(e.ToString());
                WriteLog(true);
            }
        }


        class Move
        {
            public Move(float fromX, float fromY, float fromZ, float toX, float toY, float toZ, float e, float f)
            {
                From = new Point3(fromX, fromY, fromZ);
                To = new Point3(toX, toY, toZ);
                E = e;
                F = f;
            }

            public readonly Point3 From;
            public readonly Point3 To;
            public readonly float E;
            public readonly float F;
        }


        readonly struct Point2
        {
            public Point2(float x, float y)
            {
                X = x;
                Y = y;
            }

            public readonly float X;
            public readonly float Y;
        }


        readonly struct Point3
        {
            public Point3(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public readonly float X;
            public readonly float Y;
            public readonly float Z;
        }


        string[] GetFileContents(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                Abort("Need path to gcode file as first parameter");
            }

            FILENAME = args[0];

            if (!File.Exists(FILENAME))
            {
                Abort($"File '{FILENAME}' not found");
            }

            Log($"Reading file {FILENAME}");
            return File.ReadAllLines(FILENAME);
        }


        string GetPath(string orig, string filenameSuffix, string extension = null)
        {
            var dir = Path.GetDirectoryName(orig);
            var fn = Path.GetFileNameWithoutExtension(orig);
            var ext = extension ?? Path.GetExtension(orig);

            return Path.Combine(dir, $"{fn}{filenameSuffix}{ext}");
        }


        void InitAndSanityCheck(string[] fileContents)
        {
            if (fileContents == null || fileContents.Length == 0)
            {
                Abort("No gcode");
            }

            for (var i = 0; i < fileContents.Length; ++i)
            {
                var line = fileContents[i] = fileContents[i].Trim();

                switch (line)
                {
                    case MAIN_START_MARKER:
                        MAIN_START_MARKER_I = i;
                        break;
                    case MAIN_END_MARKER:
                        MAIN_END_MARKER_I = i;
                        break;
                    case BL_START_MARKER:
                        BL_START_MARKER_I = i;
                        break;
                    case BL_END_MARKER:
                        BL_END_MARKER_I = i;
                        break;
                }
            }

            if (MAIN_START_MARKER_I < 0)
            {
                Abort($"Marker '{MAIN_START_MARKER}' not found");
            }

            if (MAIN_END_MARKER_I < 0)
            {
                Abort($"Marker '{MAIN_END_MARKER}' not found");
            }

            if (BL_START_MARKER_I < 0)
            {
                Abort($"Marker '{BL_START_MARKER}' not found");
            }

            if (BL_END_MARKER_I < 0)
            {
                Abort($"Marker '{BL_END_MARKER}' not found");
            }

            if (BL_START_MARKER_I >= BL_END_MARKER_I)
            {
                Abort($"Marker '{BL_START_MARKER}' must come before '{BL_END_MARKER}'");
            }

            if (BL_START_MARKER_I >= MAIN_END_MARKER_I || BL_END_MARKER_I >= MAIN_END_MARKER_I)
            {
                Abort($"Markers '{BL_START_MARKER}' and '{BL_END_MARKER}' must come before '{MAIN_START_MARKER}'");
            }
        }


        /// Get all the extruding G1 moves from the main part of the file
        Move[] GetRelevantMoves(string[] fileContents)
        {
            var raw = fileContents
                .Skip(MAIN_START_MARKER_I + 1)  // skip start code
                .Take(MAIN_END_MARKER_I - MAIN_START_MARKER_I)    // skip end code
                .Select(line =>
                {
                    // strip out comments and leading/trailing whitespace
                    var idx = line.IndexOf(';');
                    if (idx == 0)
                    {
                        return "";
                    }

                    if (idx > 0)
                    {
                        line = line.Substring(0, idx);
                    }

                    return line.Trim();
                })
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => line.StartsWith("G1"))
                .ToList()
                ;

            var moves = ParseG1(raw)
                   .Where(x => x.To.Z <= MAX_HEIGHT) // only moves below a certain height
                   .Where(x => x.E > 0 && !x.To.Equals(x.From)) // filter out feedrate changes, travel and unretraction moves
                   .ToArray();

            if (moves.Length == 0)
            {
                Abort("Found zero relevant moves. Make sure you have the marker comments in the right places.");
            }

            Log($"Found {moves.Length} relevant G1 commands ({raw.Count} total).");

            return moves;
        }


        /// Parse the G1 moves
        IEnumerable<Move> ParseG1(IEnumerable<string> lines)
        {
            Log("Parsing G1 commands");

            float curX = 0, curY = 0, curZ = 0;

            foreach (ReadOnlySpan<char> line in lines)
            {
                float toX = curX, toY = curY, toZ = curZ, e = 0, f = 0;

                var i = 0;
                var l = line;

                // parse from the back to sidestep some whitespace handling
                while ((i = l.LastIndexOfAny("XYZEF".AsSpan())) >= 0)
                {
                    var a = l[i];

                    var v = float.Parse(l.Slice(i + 1), numberStyle, formatInfo);
                    l = l.Slice(0, i);

                    switch (a)
                    {
                        case 'X':
                            toX = v;
                            break;
                        case 'Y':
                            toY = v;
                            break;
                        case 'Z':
                            toZ = v;
                            break;
                        case 'E':
                            e = v;
                            break;
                        case 'F':
                            f = v;
                            break;
                    }
                }

                yield return new Move(curX, curY, curZ, toX, toY, toZ, e, f);
                curX = toX;
                curY = toY;
                curZ = toZ;
            }
        }


        /// Get the points of the levelling grid
        Point2[,] GetLevellingGridPoints()
        {
            Log("Generating levelling grid");

            var p = new Point2[POINTS_Y, POINTS_X];

            for (var y = 0; y < POINTS_Y; ++y)
            {
                for (var x = 0; x < POINTS_X; ++x)
                {
                    p[y, x] = new Point2(INSET_LEFT + x * POINT_DX, INSET_FRONT + y * POINT_DY);
                }
            }

            return p;
        }


        /// Get the areas used by the print. Each area is defined as a rectangle with levelling points at the corners and no levelling points inside.
        bool[,] GetUsedAreas(IEnumerable<Move> moves)
        {
            // draw the moves as white lines on a black bitmap image
            // then check which areas have any non-black pixels

            Log("Computing used area of the bed");

            var res = new bool[POINTS_Y - 1, POINTS_X - 1];
            using var bmp = new Bitmap(BEDSIZE_X, BEDSIZE_Y, PixelFormat.Format32bppRgb); // unfortunately, Graphics does not support indexed or grayscale
            using var p = new Pen(Color.White, 1);

            // draw all the extruding moves
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                g.Clear(Color.Black);
                foreach (var move in moves)
                {
                    g.DrawLine(p, move.From.X, move.From.Y, move.To.X, move.To.Y);
                }
            }

            var bits = bmp.LockBits(new Rectangle(0, 0, BEDSIZE_X, BEDSIZE_Y), ImageLockMode.ReadOnly, bmp.PixelFormat);

            unsafe
            {
                var d = new ReadOnlySpan<UInt32>(bits.Scan0.ToPointer(), bits.Width * bits.Height);
                int left = 0, right = 0, front = 0, back = 0;

                // Area is used, if there is any pixel that has non-zero color data
                bool isAreaUsed(ReadOnlySpan<UInt32> d, int px, int py)
                {
                    for (var y = front; y < back; ++y)
                    {
                        for (var x = left; x < right; ++x)
                        {
                            if ((d[bits.Width * y + x] & 0x00FFFFFF) != 0)
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                for (var py = 0; py < POINTS_Y - 1; ++py)
                {
                    for (var px = 0; px < POINTS_X - 1; ++px)
                    {
                        // need to special-case the borders, because of the inset
                        left = (int)Math.Floor(px * POINT_DX + (px > 0 ? INSET_LEFT : 0));
                        right = (int)Math.Ceiling(left + POINT_DX + (px == 0 ? INSET_LEFT : 0) + (px == POINTS_X - 2 ? INSET_RIGHT : 0));
                        front = (int)Math.Floor(py * POINT_DY + (py > 0 ? INSET_FRONT : 0));
                        back = (int)Math.Ceiling(front + POINT_DY + (py == 0 ? INSET_FRONT : 0) + (py == POINTS_Y - 2 ? INSET_BACK : 0));

                        res[py, px] = isAreaUsed(d, px, py);
                    }
                }
            }

            bmp.UnlockBits(bits);

            var num = res.Cast<bool>().Aggregate(0, (a, b) => b ? a + 1 : a);
            Log($"{num} of {(POINTS_Y - 1) * (POINTS_X - 1)} areas used.");

            return res;
        }


        /// Get the points to level from the used areas
        bool[,] GetUsedLevellingPoints(bool[,] usedAreas)
        {
            Log("Computing points to probe.");

            var res = new bool[POINTS_Y, POINTS_X];

            for (var y = 0; y < POINTS_Y - 1; ++y)
            {
                for (var x = 0; x < POINTS_X - 1; ++x)
                {
                    if (usedAreas[y, x])
                    {
                        res[(y + 0), (x + 0)] = true;
                        res[(y + 0), (x + 1)] = true;
                        res[(y + 1), (x + 0)] = true;
                        res[(y + 1), (x + 1)] = true;
                    }
                }
            }

            var num = res.Cast<bool>().Aggregate(0, (a, b) => b ? a + 1 : a);
            Log($"{num} of {POINTS_Y * POINTS_X} points are to be probed.");

            return res;
        }


        /// Generate reduced levelling gcode
        string GenerateLevellingGcode(Point2[,] grid, bool[,] usedPoints)
        {
            Log("Generate bed levelling gcode.");
            var sb = new StringBuilder();

            sb.AppendLine("; reduced levelling start");
            sb.AppendLine("G29 P0 ; zero mesh and turn off");
            sb.AppendLine("; invalidate the point nearest to XY");

            for (int y = 0; y < POINTS_Y; ++y)
            {
                for (int x = 0; x < POINTS_X; ++x)
                {
                    if (usedPoints[y, x])
                    {
                        sb.AppendLine($"G29 I1 X{grid[y, x].X:###.###} Y{grid[y, x].Y:###.###}");
                    }
                }
            }

            sb.AppendLine("G29 P1 C ; probe all invalid");
            sb.AppendLine("G29 P3 ; fill unpopulated");
            sb.AppendLine("G29 A ; activate");
            sb.AppendLine("G29 T0 ; output mesh");
            sb.AppendLine("; reduced levelling end");

            var result = sb.ToString();
            Log(result);

            return result;
        }


        /* 
        DEBUG: 
        output a bitmap with all points and inset and moves and active areas drawn 
        output a text with the generated g-codes
        */
        void OutputDebugData(Point2[,] grid, bool[,] usedAreas, bool[,] usedPoints, IEnumerable<Move> moves)
        {
            if (!debug)
            {
                return;
            }

            Log("Generating visualization.");
            using var bmp = new Bitmap(BEDSIZE_X, BEDSIZE_Y, PixelFormat.Format24bppRgb);

            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.Clear(Color.White);

                // draw area outside of the mesh inset
                g.FillRectangle(Brushes.DarkGray, 0, 0, BEDSIZE_X, INSET_FRONT);
                g.FillRectangle(Brushes.DarkGray, 0, BEDSIZE_Y - INSET_BACK, BEDSIZE_X, INSET_BACK);
                g.FillRectangle(Brushes.DarkGray, 0, 0, INSET_LEFT, BEDSIZE_Y);
                g.FillRectangle(Brushes.DarkGray, BEDSIZE_X - INSET_RIGHT, 0, INSET_RIGHT, BEDSIZE_Y);

                // draw areas
                for (var y = 0; y < POINTS_Y - 1; ++y)
                {
                    for (var x = 0; x < POINTS_X - 1; ++x)
                    {
                        var p1 = grid[y, x];
                        var p2 = grid[y + 1, x + 1];
                        g.FillRectangle(usedAreas[y, x] ? Brushes.LightPink : Brushes.LightGray, p1.X, p1.Y, p2.X - p1.X, p2.Y - p1.Y);
                    }
                }

                // draw moves
                using var movePen = new Pen(Color.Black, 1);
                foreach (var m in moves)
                {
                    g.DrawLine(movePen, m.From.X, m.From.Y, m.To.X, m.To.Y);
                }

                // draw inset border
                using var insetPen = new Pen(Color.Black, 1);
                g.DrawRectangle(insetPen, INSET_LEFT, INSET_FRONT, BEDSIZE_X - INSET_LEFT - INSET_RIGHT, BEDSIZE_Y - INSET_FRONT - INSET_BACK);

                // draw levelling points
                for (var y = 0; y < POINTS_Y; ++y)
                {
                    for (var x = 0; x < POINTS_X; ++x)
                    {
                        var p = grid[y, x];
                        g.FillEllipse(usedPoints[y, x] ? Brushes.Red : Brushes.Black, p.X - 2, p.Y - 2, 4, 4);
                    }
                }
            }

            bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);
            var fn = GetPath(FILENAME, "_debug", ".png");
            bmp.Save(fn);
            Log($"Saved visualization to {fn}");
        }


        void WriteOutput(string[] fileContents, string levellingGcode)
        {
            var fn = GetPath(FILENAME, "_level");

            using (var f = File.CreateText(fn))
            {
                // copy head
                for (var i = 0; i < BL_START_MARKER_I; ++i)
                {
                    f.WriteLine(fileContents[i]);
                }

                //insert levelling
                f.Write(levellingGcode);

                // copy rest
                for (var i = BL_END_MARKER_I + 1; i < fileContents.Length; ++i)
                {
                    f.WriteLine(fileContents[i]);
                }
            }

            Log($"Saved modified gcode to {fn}");
        }


        void Log(string s)
        {
            Console.WriteLine(s);
            _Log.AppendLine(s);
        }


        void WriteLog(bool hasError = false)
        {
            if (FILENAME != null && (debug || hasError))
            {
                File.WriteAllText(GetPath(FILENAME, "_debug", ".txt"), _Log.ToString());
            }
        }

        void Abort(string reason)
        {
            Log($"Error: {reason}");
            WriteLog(true);
            Environment.Exit(1);
        }
    }
}
