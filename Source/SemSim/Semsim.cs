using System.Diagnostics;
using System.Text.Json;
using Microsoft.Boogie;


namespace SemSim
{
  public class Semsim
  {
    private static void Usage()
    {
      Console.WriteLine("semsim - Compute semantic similarity between bpl functions.");
      Console.WriteLine("Usage: semsim <query.bpl> <target.bpl>");
      Console.WriteLine("    or semsim <bpl_text1> <bpl_text2>");
      Console.WriteLine("    or semsim <bpl_code.jsonl> <semsims.txt>");
      Console.WriteLine("    or semsim <bpl_code_dir> <funcs_file> <bb_id_map> <func_groups> <temp_dir> <semsims.txt> <pair_per_group> <num_process>");
    }

    static int Main(string[] args)
    {
      if (args[0].EndsWith(".bpl"))
      {
        string qtext = File.ReadAllText(args[0]);
        string ttext = File.ReadAllText(args[1]);
        float sim = BplMatch.RunMatch(qtext, ttext);
        Console.Out.WriteLine($"sim: {sim}");
      }
      else if (args[0].EndsWith(".jsonl"))
      {
        ComputeSemsimsForFile(args[0], args[1]);
      }
      else if (args.Length == 2)
      {
        float sim = BplMatch.RunMatch(args[0], args[1]);
        Console.Out.WriteLine($"sim: {sim}");
      }
      else if (args.Length == 8)
      {
        ComputeSemsimsForGroups(args[0], args[1], args[2],
          args[3], args[4], args[5], int.Parse(args[6]), int.Parse(args[7])
        );
      }
      else
      {
        Usage();
        return -1;
      }

      return 0;
    }

    private static void ComputeSemsimsForFile(string bpl_code_file, string semsims_file)
    {
      Dictionary<string, string> id2bpl = new Dictionary<string, string>();

      using (StreamReader reader = new StreamReader(bpl_code_file))
      {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
          BplItem item = JsonSerializer.Deserialize<BplItem>(line);
          id2bpl[item.id] = item.bpl;
        }
      }

      List<List<string>> pairs = SamplePairs(id2bpl.Keys.ToList());

      using (StreamWriter writer = new StreamWriter(semsims_file))
      {
        for (int i = 0; i < pairs.Count; ++i)
        {
          float sim = BplMatch.RunMatch(id2bpl[pairs[i][0]], id2bpl[pairs[i][1]]);
          writer.WriteLine(string.Format("{0} {1} {2}", pairs[i][0], pairs[i][1], sim));
          writer.Flush();
        }
      }
    }

    private static List<List<string>> SamplePairs(List<string> bbIds)
    {
      var func2groups = new Dictionary<string, List<string>>();
      foreach (var bbId in bbIds)
      {
        var prefixIdx = bbId.IndexOf(".ll");
        var suffixIdx = bbId.IndexOf('.', prefixIdx + 3);
        var func = bbId.Substring(0, suffixIdx);

        if (func2groups.ContainsKey(func))
        {
          func2groups[func].Add(bbId);
        }
        else
        {
          func2groups[func] = new List<string> { bbId };
        }
      }

      var pairs = new List<List<string>>();
      var random = new Random(0);
      foreach (var groups in func2groups.Values)
      {
        var pars = new List<List<string>>();
        for (int i = 0; i < groups.Count - 1; i++)
        {
          for (int j = i + 1; j < groups.Count; j++)
          {
            pars.Add(new List<string> { groups[i], groups[j] });
          }
        }

        pars = pars.OrderBy(x => random.Next()).ToList();
        pars = pars.Take(Math.Min(20, pars.Count)).ToList();
        pairs.AddRange(pars);
      }

      // pairs = pairs.OrderBy(x => random.Next()).ToList();
      // pairs = pairs.Take(Math.Min(200, pairs.Count)).ToList();

      return pairs;
    }

    class BplItem
    {
      public string id { get; set; }
      public string bpl { get; set; }
    }

    private static Dictionary<int, List<Tuple<int, string>>> BuildBplCodeDict(string bpl_code_dir, string funcs_file, string bb_id_map)
    {
      List<FuncItem> funcs = File.ReadAllLines(funcs_file).Select(line => JsonSerializer.Deserialize<FuncItem>(line)).ToList();

      Dictionary<string, int> bb2id = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(bb_id_map));
      string[] bpl_files = Directory.GetFiles(bpl_code_dir, "*.jsonl", SearchOption.AllDirectories);
      Dictionary<int, string> bbid2bpl = new Dictionary<int, string>();
      
      Utils utils = new Utils();
      Program program = null;
      
      foreach (var file in bpl_files)
      {
        foreach (var line in File.ReadAllLines(file))
        {
          var item = JsonSerializer.Deserialize<BplItem>(line);
          if (bb2id.ContainsKey(item.id) && utils.ParseProgram(item.bpl, out program))
          {
            bbid2bpl[bb2id[item.id]] = item.bpl;
          }
        }
      }

      Dictionary<int, List<Tuple<int, string>>> funcid2bpls = new Dictionary<int, List<Tuple<int, string>>>();
      foreach (var func in funcs)
      {
        var bpls = func.blocks.Where(i => bbid2bpl.ContainsKey(i))
                              .Select(i => new Tuple<int, string>(i, bbid2bpl[i]))
                              .ToList();
        funcid2bpls[func.id] = bpls;
      }
      return funcid2bpls;
    }

    private static void ComputeSemsimsForGroups(string bpl_code_dir, string funcs_file,
      string bb_id_map, string func_groups, string temp_dir, string semsims_file, 
      int pair_per_group, int num_process)
    {
      Console.Out.WriteLine("Reading Bpl codes and groups info...");

      var funcid2bpls = BuildBplCodeDict(bpl_code_dir, funcs_file, bb_id_map);
      List<List<int>> groups = File.ReadAllLines(func_groups)
                                   .Where(line => !string.IsNullOrWhiteSpace(line))
                                   .Select(line => line.Split(' ').Select(s => int.Parse(s)).ToList())
                                   .ToList();

      Func<List<int>, List<Tuple<Tuple<int, string>, Tuple<int, string>>>> samplePairsFromGroup = group =>
      {
        var bpls = group.SelectMany(i => funcid2bpls[i]).ToList();
        var pairs = new List<Tuple<Tuple<int, string>, Tuple<int, string>>>();
        for (int i = 0; i < bpls.Count - 1; i++)
        {
          for (int j = i + 1; j < bpls.Count; j++)
          {
            pairs.Add(new Tuple<Tuple<int, string>, Tuple<int, string>>(bpls[i], bpls[j]));
          }
        }
        var random = new Random();
        pairs = pairs.OrderBy(x => random.Next()).ToList();
        pairs = pairs.Take(Math.Min(pair_per_group, pairs.Count)).ToList();

        return pairs;
      };

      Action<List<List<int>>, string, System.Action> computeSemsimForSmallGroups = (sgroups, res_file, group_done) =>
      {
        using (StreamWriter writer = new StreamWriter(res_file))
        {
          foreach (var group in sgroups)
          {
            var pairs = samplePairsFromGroup(group);
            foreach (var pair in pairs)
            {
              float sim = BplMatch.RunMatch(pair.Item1.Item2, pair.Item2.Item2);
              writer.WriteLine(string.Format("{0} {1} {2}", pair.Item1.Item1, pair.Item2.Item1, sim));
            }
            writer.Flush();
            group_done();
          }
        }
      };

      int done = 0;
      DateTime beg_time = DateTime.Now;
      System.Action group_done_callback = () => {
        Interlocked.Increment(ref done);

        TimeSpan elapsed = DateTime.Now - beg_time;
        long avg_time = (long)(elapsed.TotalSeconds / done);
        TimeSpan eta = TimeSpan.FromSeconds(avg_time*(groups.Count - done));
        string log_str = string.Format("\r{0,10}/{1} groups {2} s/group {3:hh\\:mm\\:ss} elapsed {4:dd\\:hh\\:mm\\:ss} ETA",
          done, groups.Count, avg_time, elapsed, eta);
        
        Console.Out.Write(log_str);
      };

      Console.Out.WriteLine("Computing semsim...");

      int groupPerProcess = groups.Count / num_process;
      List<Task> tasks = new List<Task>();
      for (int i = 0; i < num_process; ++i)
      {
        var sgroups = groups.GetRange(i * groupPerProcess, Math.Min(groupPerProcess, groups.Count - i * groupPerProcess));
        tasks.Add(Task.Run(() => computeSemsimForSmallGroups(
          sgroups, 
          Path.Join(temp_dir, "semsims_" + i.ToString() + ".txt"),
          group_done_callback
        )));
      }
      Task.WaitAll(tasks.ToArray());

      Console.Out.WriteLine("Gathering results...");
      using (StreamWriter writer = new StreamWriter(semsims_file))
      {
        for (int i = 0; i < num_process; ++i)
        {
          string text = File.ReadAllText(Path.Join(temp_dir, "semsims_" + i.ToString() + ".txt"));
          writer.Write(text);
        }
      }
    }

    class FlowItem
    {
      public List<int> uv { get; set; }
      public int type { get; set; }
    }

    class FuncItem
    {
      public int id { get; set; }
      public List<int> blocks { get; set; }
      public List<FlowItem> flows { get; set; }
    }
  }
}