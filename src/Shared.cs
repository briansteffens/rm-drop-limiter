using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Data.SqlClient;



namespace RmDropLimiter {



// Raised when no *.state file could be found. One has been written, so the
// program should just exit.
class StateNotFoundException : Exception { }


// Represents a unit of time, which must be cleanly convertible to seconds.
public enum TimeUnit
{
    sec  = 1,
    min  = 60,
    hour = 60 * 60,
    day  = 60 * 60 * 24,
    week = 60 * 60 * 24 * 7,
    year = 60 * 60 * 24 * 7 * 52
}


static class ENV
{
    public const string FILE_PREFIX = "rm-drop-limiter";

    // The working directory of the program.
    public static string PATH { get {
        string ret = Path.GetDirectoryName(new Uri(
            Assembly.GetAssembly(typeof(ENV)).CodeBase).LocalPath);

        if (!ret.EndsWith(Path.DirectorySeparatorChar.ToString()))
            ret += Path.DirectorySeparatorChar;

        return ret;
    } }

    // DateTime.Now.Ticks in seconds
    public static long NOW { get {
        return DateTime.Now.Ticks / 10000000;
    } }

    // Environment-appropriate newline string
    public static string NL { get { return Environment.NewLine; } }

    // If the path is not already an absolute path, prefix it with ENV.PATH
    public static string PATH_ABS(string path)
    {
        if (path.StartsWith("/"))               // /some/abs/path
            return path;

        if (path.Length >= 2 && path[1] == ':') // C:\dir\file
            return path;

        return ENV.PATH + path;
    }
}


// Represents a length of time as a unit of time and a count.
public class TimeFrame
{
    public long Count { get; set; }
    public TimeUnit Unit { get; set; }

    // Parse a string like 'sec' or '5min' into a TimeFrame instance.
    public static TimeFrame Parse(string input)
    {
        string count = null;
        foreach (Match m in new Regex("^[0-9]+").Matches(input))
        {
            count = m.Value;
            break;
        }

        var ret = new TimeFrame();

        if (count == null)
            ret.Count = 1;
        else
        {
            ret.Count = long.Parse(count);
            input = input.Replace(count, "");
        }

        ret.Unit = input.ToTimeUnit();

        return ret;
    }

    public long ToSeconds()
    {
        return Count * (long)Unit;
    }

    public override string ToString()
    {
        return Count.ToString() + Unit.ToString();
    }
}


static class ExtensionMethods
{
    // Convert a string like 'sec' or 'min' to a TimeUnit.
    public static TimeUnit ToTimeUnit(this string input)
    {
        return (TimeUnit)Enum.Parse(typeof(TimeUnit), input);
    }
}


public class Logger
{
    public List<LogWriter> Writers { get; protected set; }

    public Logger()
    {
        Writers = new List<LogWriter>();
    }

    public void Log(string format, params object[] args)
    {
        LogPart(format + ENV.NL, args);
    }

    public void LogPart(string format, params object[] args)
    {
        string data = string.Format(format, args);

        foreach (var writer in Writers)
            writer.Write(data);
    }
}


public interface LogWriter
{
    void Write(string data);
}


class NullLogWriter : LogWriter
{
    public void Write(string data) {}
}


class ConsoleLogWriter : LogWriter
{
    public void Write(string data)
    {
        Console.Write(data);
    }
}


class FileLogWriter : LogWriter
{
    public string Filename { get; protected set; }

    public FileLogWriter(string filename = null)
    {
        Filename = ENV.PATH_ABS(filename ?? ENV.FILE_PREFIX + ".log");
    }

    public void Write(string data)
    {
        File.AppendAllText(Filename, data);
    }
}


// Represents an item's state in the database.
class ItemDBState
{
    // The number of items in-game currently.
    public int Count { get; set; }

    // The current limit on this item.
    public int Limit { get; set; }

    // The difference between Limit and Count is the pool size, or the number
    // of drops waiting to be spawned into the world.
    public int Pool { get { return Limit > Count ? Limit - Count : 0; } }
}


class GameDB : IDisposable
{
    public Config Config { get; protected set; }

    public Dictionary<int,ItemDBState> DBState { get; protected set; }

    protected DbConnection Conn { get; set; }

    public GameDB(Config config = null)
    {
        Config = config ?? new Config();

        switch (Config.DatabaseDriver.Trim().ToLower())
        {
        case "sql":
            Conn = new SqlConnection(Config.ConnectionString);
            break;
        case "odbc":
            Conn = new OdbcConnection(Config.ConnectionString);
            break;
        default:
            throw new Exception("In config file, DatabaseDriver of [" +
                                Config.DatabaseDriver + "] is invalid.");
        }

        Conn.Open();

        UpdateDatabaseState();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            // managed resources
        }

        // unmanaged resources

        if (Conn != null)
            Conn.Close();

        Conn = null;
    }

    protected virtual DbCommand CreateCommand(string sql, params object[] p)
    {
        var ret = Conn.CreateCommand();

        ret.CommandText = sql;

        if (p.Length == 0)
            return ret;

        for (int i = 0; i < p.Length; i++)
        {
            var param = ret.CreateParameter();

            if (p[i].GetType() == typeof(int))
                param.DbType = DbType.Int32;
            else
                throw new Exception("Type " + p[i].GetType().Name +
                                    " cannot be handled by CreateCommand().");

            param.Value = p[i];

            ret.Parameters.Add(param);
        }

        return ret;
    }

    public int Command(string sql, params object[] p)
    {
        return CreateCommand(sql, p).ExecuteNonQuery();
    }

    public object Scalar(string sql, params object[] p)
    {
        return CreateCommand(sql, p).ExecuteScalar();
    }

    public T Scalar<T>(string sql, params object[] p)
    {
        return (T)Scalar(sql, p);
    }

    public IEnumerable<Dictionary<string, object>> Results(
        string sql, params object[] p)
    {
        using (var reader = CreateCommand(sql, p).ExecuteReader())
            while (reader.Read())
            {
                var ret = new Dictionary<string, object>();

                for (int i = 0; i < reader.FieldCount; i++)
                    ret.Add(reader.GetName(i), reader[i]);

                yield return ret;
            }
    }

    protected ItemDBState GetOrCreate(int item_index)
    {
        if (!DBState.ContainsKey(item_index))
            DBState.Add(item_index, new ItemDBState());

        return DBState[item_index];
    }

    public void UpdateDatabaseState()
    {
        DBState = new Dictionary<int,ItemDBState>();

        foreach (var r in Results("select ItemIndex as i, count(1) as c " +
                                  "from tblSpecialItem1 group by ItemIndex;"))
            GetOrCreate((int)r["i"]).Count = (int)r["c"];

        foreach (var r in Results("select ItemIndex as i, " +
                                  "ItemCountLimit as l " +
                                  "from tblSpecialItemLimit1;"))
            GetOrCreate((int)r["i"]).Limit = (int)r["l"];
    }

    public void IncrementLimits(Dictionary<int,int> delta)
    {
        foreach (int item in delta.Keys)
            Command("update tblSpecialItemLimit1 " +
                    "set ItemCountLimit = ItemCountLimit + ? " +
                    "where ItemIndex = ?;",
                        delta[item],
                        item);
    }
}


// Code for managing the database patch.
static class DBPatch
{
    const string TRIGGER_NAME = "RMT_DROPLIMITER_DECREMENT_LIMIT";

    public static bool IsInstalled(GameDB db)
    {
        object check = db.Scalar(
            "select name from sysobjects " +
            "where name = '" + TRIGGER_NAME + "' " +
            "and type = 'TR';"
        );

        return check != null && !(check is DBNull);
    }

    public static void Install(GameDB db)
    {
        db.Command(
            "create trigger " + TRIGGER_NAME + " " +
            "on tblSpecialItem1 " +
            "for delete " +
            "as " +
                "update tblSpecialItemLimit1 " +
                "set ItemCountLimit = ItemCountLimit - (" +
                    "select count(1) from deleted d " +
                    "where d.ItemIndex = tblSpecialItemLimit1.ItemIndex" +
                ")" +
            ";"
        );
    }

    public static void Reinstall(GameDB db)
    {
        Uninstall(db);
        Install(db);
    }

    public static void Uninstall(GameDB db)
    {
        db.Command("drop trigger " + TRIGGER_NAME + ";");
    }
}


// Represents a single item drop event in the state history.
class DropEvent
{
    public int ItemIndex { get; set; }
    public long Timestamp { get; set; }
}


// A *.state file, which stores the drop history and the timestamp of the last
// run.
class State
{
    public string Filename { get; protected set; }

    public long LastRun { get; set; }
    public List<DropEvent> History { get; protected set; }

    public State(string filename = null)
    {
        Filename = filename ?? ENV.PATH + ENV.FILE_PREFIX + ".state";

        History = new List<DropEvent>();

        if (!File.Exists(Filename))
        {
            File.WriteAllText(Filename, ENV.NOW.ToString());
            throw new StateNotFoundException();
        }

        int line_index = 0;
        foreach (string line in File.ReadAllLines(Filename))
        {
            if (line_index == 0)
                LastRun = long.Parse(line);
            else
            {
                var parts = line.Split('|');
                History.Add(new DropEvent() {
                    ItemIndex = int.Parse(parts[0]),
                    Timestamp = long.Parse(parts[1])
                });
            }

            line_index++;
        }
    }

    public void Save(string filename = null)
    {
        var sb = new StringBuilder();

        sb.AppendFormat("{0}{1}", LastRun, ENV.NL);

        foreach (var h in History)
            sb.AppendFormat("{0}|{1}{2}", h.ItemIndex, h.Timestamp, ENV.NL);

        File.WriteAllText(filename ?? Filename, sb.ToString());
    }

    // Delete all DropEvents that are stale based on drop.Limit, using now as
    // the current time.
    public int Expire(Drop drop, long now)
    {
        if (drop.Limit == null)
            return 0;

        var to_delete = new List<DropEvent>();

        long oldest = drop.Limit.TimeFrame.ToSeconds();

        foreach (var h in History)
            if (h.ItemIndex == drop.ItemIndex)
                if (now - h.Timestamp > oldest)
                    to_delete.Add(h);

        foreach (var h in to_delete)
            History.Remove(h);

        return to_delete.Count;
    }

    // Returns true if drop.Limit has already been reached.
    public bool IsMaxed(Drop drop)
    {
        if (drop.Limit == null)
            return false;

        int count = 0;

        foreach (var h in History)
           if (h.ItemIndex == drop.ItemIndex)
                count++;

        return count >= drop.Limit.Max;
    }
}


// A *.conf file - general program configuration
class Config : Dictionary<string, string>
{
    public string ConnectionString { get { return this["ConnectionString"]; } }
    public string DatabaseDriver { get { return this["DatabaseDriver"]; } }
    public string DatabasePatch { get { return this["DatabasePatch"]; } }

    public Config(string filename = null)
    {
        if (filename == null)
            filename = ENV.PATH + ENV.FILE_PREFIX + ".conf";

        foreach (string line_ in File.ReadAllLines(filename))
        {
            string line = line_.Trim();

            if (line == "" || line.StartsWith("#"))
                continue;

            var parts = new List<string>(line.Split('='));

            if (parts.Count < 2)
                throw new Exception("In [" + filename + "], " +
                                    "unable to parse [" + line + "].");

            string key = parts[0].Trim();

            parts.RemoveAt(0);

            string val = string.Join("=", parts.ToArray()).Trim();

            this[key] = val;
        }
    }
}


// A quantity over a TimeFrame, representing a maximum number of drops over a
// given period of time.
public class Limit
{
    public int Max { get; set; }
    public TimeFrame TimeFrame { get; set; }
}


// A percentage (Rate) giving the likelihood for an item to be added to the
// drop pool over the given period of time (TimeFrame).
public class DropRate
{
    public decimal Rate { get; set; }
    public TimeFrame TimeFrame { get; set; }

    public override string ToString()
    {
        return Rate.ToString() + "%/" + TimeFrame.ToString();
    }
}


// Represents the drop settings for an item.
public class Drop
{
    // A Redmoon item index (item kind '6').
    public int ItemIndex { get; protected set; }

    // For easier to read log files and console output. Can be anything.
    public string Description { get; protected set; }

    public DropRate DropRate { get; protected set; }
    public Limit Limit { get; protected set; }
    public List<string> PoolNames { get; protected set; }

    public Drop(string raw)
    {
        PoolNames = new List<string>();

        var parts0 = new List<string>(raw.Trim().Split(':'));
        Description = parts0[0].Trim();
        parts0.RemoveAt(0);
        raw = string.Join(":", parts0);

        var parts = new List<string>(
            raw.Trim().Split(new char[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries));

        ItemIndex = int.Parse(parts[0]);

        var spl = parts[1].Split('/');
        if (spl.Length != 2)
            throw new Exception("Invalid drop rate [" + parts[2] + "]. " +
                                "Expected examples: [3%/day], [5%/2hour]");

        // Calculate DropRate
        DropRate = new DropRate {
            Rate = decimal.Parse(spl[0].Replace("%", "")),
            TimeFrame = TimeFrame.Parse(spl[1])
        };

        parts.RemoveAt(0);
        parts.RemoveAt(0);

        while (parts.Count >= 2)
        {
            switch (parts[0].Trim().ToLower())
            {
            case "limit":
                spl = parts[1].Split('/');

                if (spl.Length != 2)
                    throw new Exception("Invalid: [" + parts[1] + "]. " +
                                        "Expected: [limit 3/week]");

                this.Limit = new Limit {
                    Max = int.Parse(spl[0]),
                    TimeFrame = TimeFrame.Parse(spl[1])
                };
                break;

            case "pool":
                spl = parts[1].Split(',');

                foreach (string pool_name in spl)
                    this.PoolNames.Add(pool_name.Trim());

                break;

            default:
                throw new Exception("Unable to parse *.drops line: " + raw);
            }

            parts.RemoveAt(0);
            parts.RemoveAt(0);
        }
    }
}


class Pool
{
    public string Name { get; protected set; }
    public int Size { get; protected set; }

    public Pool(string raw)
    {
        var parts = raw.Split(new string[] { " ", "\t" },
                              StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 3)
            throw new Exception("Unable to parse [" + raw + "]");

        Name = parts[1].Trim();
        Size = int.Parse(parts[2]);
    }
}


// A *.drops file, which defines all of the drops managed by the program.
class Drops : List<Drop>
{
    public List<Pool> Pools { get; protected set; }

    public Drops(string filename = null)
    {
        if (filename == null)
            filename = ENV.PATH + ENV.FILE_PREFIX + ".drops";

        Pools = new List<Pool>();

        foreach (string line in File.ReadAllLines(filename))
        {
            string raw = line.Trim();

            if (raw == "" || raw.StartsWith("#"))
                continue;

            if (raw.StartsWith("drop"))
                Add(new Drop(raw));
            else if (raw.StartsWith("pool"))
                Pools.Add(new Pool(raw));
            else
                throw new Exception("Line in .drops file can't be parsed: " +
                                    "[" + raw + "].");
        }
    }

    public Drop FindByItemIndex(int itemindex)
    {
        var ret = Find(p => p.ItemIndex == itemindex);

        if (ret == null)
            throw new Exception("Drop with item index [" +
                                itemindex.ToString() + "] not found.");

        return ret;
    }

    public Pool GetPool(string pool_name)
    {
        var ret = Pools.Find(p => p.Name == pool_name);

        if (ret == null)
            throw new Exception("No pool found named " + pool_name);

        return ret;
    }

    public List<Pool> GetPools(Drop drop)
    {
        var ret = new List<Pool>();

        foreach (var pool_name in drop.PoolNames)
            ret.Add(GetPool(pool_name));

        return ret;
    }

    public List<Drop> GetDrops(Pool pool)
    {
        var ret = new List<Drop>();

        foreach (var drop in this)
            if (drop.PoolNames.Contains(pool.Name))
                ret.Add(drop);

        return ret;
    }
}


public interface Report
{
    void Drop(Drop drop, long timestamp);
}


public class NullReport : Report
{
    public void Drop(Drop drop, long timestamp) {}
}


// Allows reporting drops to a CSV file.
public class CsvReport : Report
{
    public string Filename { get; protected set; }

    public CsvReport(string filename = null)
    {
        Filename = ENV.PATH_ABS(filename ?? ENV.FILE_PREFIX + ".csv");

        if (!File.Exists(Filename))
            Row("DateTime","Timestamp","Item","ItemIndex");
    }

    protected void Row(params object[] vals)
    {
        var sb = new StringBuilder();

        bool first = true;
        foreach (object val_ in vals)
        {
            string val = "";

            if (val_ != null && !(val_ is DBNull))
                val = val_.ToString();

            val = val.Replace("\"", "\"\"");
            sb.AppendFormat("{0}\"{1}\"", (first ? "" : ","),  val);

            first = false;
        }

        sb.Append(ENV.NL);

        File.AppendAllText(Filename, sb.ToString());
    }

    public void Drop(Drop drop, long timestamp)
    {
        var dt = new DateTime(timestamp * 10000000);

        Row(dt.ToString("MM/dd/yyyy HH:mm:ss"),
            timestamp,
            drop.Description,
            drop.ItemIndex);
    }
}


// The main interface for the code. Instantiate and call Run().
class DropLimiter
{
    public Config Config { get; protected set; }
    public State State { get; protected set; }
    public Logger Logger { get; protected set; }
    public GameDB GameDB { get; protected set; }
    public Report Report { get; set; }

    protected Random Random { get; set; }

    public DropLimiter(Config config = null,
                       State state = null,
                       Logger logger = null,
                       GameDB gamedb = null,
                       Report report = null)
    {
        this.Config = config ?? new Config();
        this.State = state ?? new State();
        this.Logger = logger ?? new Logger();
        this.GameDB = gamedb ?? new GameDB();
        this.Report = report ?? new NullReport();

        this.Random = new Random();
    }

    // The dice throw
    public bool RandomTest(decimal rate)
    {
        return (decimal)Random.NextDouble() <= rate * .01m;
    }

    // Performs drop logic and returns a delta to be applied to the database
    // of [item indexes] to [item counts]. Does not save State on its own.
    public Dictionary<int,int> Run(Drops drops)
    {
        long now = ENV.NOW;
        long DELTA = now - State.LastRun;

        Logger.Log(ENV.NL + new String('=', 80));
        Logger.Log("= {0} second(s) since the last run.", DELTA);
        Logger.Log(new String('=', 80));

        var newdrops = new Dictionary<int, int>();

        GameDB.UpdateDatabaseState();

        foreach (var drop in drops)
        {
            long delta = DELTA;

            var rates = new List<decimal>();

            long timeframe = drop.DropRate.TimeFrame.ToSeconds();

            Logger.Log("{0} droprate: {1} ({2} seconds)",
                       drop.Description, drop.DropRate, timeframe);
            Logger.Log("Dice throws: {0} sec / {1} sec = {2}",
                       delta, timeframe, (decimal)delta / (decimal)timeframe);


            if (delta > timeframe)
            {
                for (int i = 0; i < delta / timeframe; i++)
                    rates.Add(drop.DropRate.Rate);

                delta = delta % timeframe;
            }

            rates.Add((decimal)delta / (decimal)timeframe *
                    drop.DropRate.Rate);

            int sim_index = 0;
            foreach (decimal rate in rates)
            {
                sim_index++;
                long sim_end = State.LastRun + (sim_index * timeframe);
                sim_end = sim_end < now ? sim_end : now;
                int expired = State.Expire(drop, sim_end);
                if (expired > 0)
                    Logger.Log("\t{0} stale drops expired from history",
                               expired);


                Logger.LogPart("\tDice throw {0}%, {1} sec ago: ",
                               rate, now - sim_end);

                if (State.IsMaxed(drop))
                {
                    Logger.Log("limit reached");
                    continue;
                }

                bool double_break = false;
                foreach (var pool in drops.GetPools(drop))
                {
                    int pool_size = 0;

                    foreach (var pooldrop in drops.GetDrops(pool))
                        pool_size += GameDB.DBState[pooldrop.ItemIndex].Pool;

                    if (pool_size >= pool.Size)
                    {
                        Logger.Log("pool " + pool.Name + " full");
                        double_break = true;
                        break;
                    }
                }
                if (double_break)
                    break;

                bool outcome = RandomTest(rate);

                Logger.Log(outcome ? "drop" : "no drop");

                if (!outcome)
                    continue;

                GameDB.DBState[drop.ItemIndex].Limit++;

                if (drop.Limit != null)
                    State.History.Add(new DropEvent {
                        ItemIndex = drop.ItemIndex,
                        Timestamp = sim_end
                    });

                if (!newdrops.ContainsKey(drop.ItemIndex))
                    newdrops.Add(drop.ItemIndex, 0);

                newdrops[drop.ItemIndex] = newdrops[drop.ItemIndex] + 1;

                Report.Drop(drop, sim_end);
            }

            Logger.Log(new String('-', 70));
        }

        State.LastRun = now;

        return newdrops;
    }
}



}
