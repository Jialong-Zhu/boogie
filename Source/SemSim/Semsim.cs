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
      // Console.WriteLine("    or semsim <bpl_code.jsonl> <semsims.txt>");
      Console.WriteLine("    or semsim <func2bpls> <func_groups> <temp_dir> <pair_per_group> <world_size> <rank>");
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
      // else if (args[0].EndsWith(".jsonl"))
      // {
      //   ComputeSemsimsForFile(args[0], args[1]);
      // }
      else if (args.Length == 2)
      {
        float sim = BplMatch.RunMatch(args[0], args[1]);
        Console.Out.WriteLine($"sim: {sim}");
      }
      else if (args.Length == 6)
      {
        ComputeSemsimsForGroups(args[0], args[1], args[2], 
          int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5])
        );
      }
      else
      {
        Usage();
        return -1;
      }

      return 0;
    }

    // private static void ComputeSemsimsForFile(string bpl_code_file, string semsims_file)
    // {
    //   Dictionary<string, string> id2bpl = new Dictionary<string, string>();

    //   using (StreamReader reader = new StreamReader(bpl_code_file))
    //   {
    //     string? line;
    //     while ((line = reader.ReadLine()) != null)
    //     {
    //       BplItem item = JsonSerializer.Deserialize<BplItem>(line);
    //       id2bpl[item.id] = item.bpl;
    //     }
    //   }

    //   List<List<string>> pairs = SamplePairs(id2bpl.Keys.ToList());

    //   using (StreamWriter writer = new StreamWriter(semsims_file))
    //   {
    //     for (int i = 0; i < pairs.Count; ++i)
    //     {
    //       float sim = BplMatch.RunMatch(id2bpl[pairs[i][0]], id2bpl[pairs[i][1]]);
    //       writer.WriteLine(string.Format("{0} {1} {2}", pairs[i][0], pairs[i][1], sim));
    //       writer.Flush();
    //     }
    //   }
    // }

    // private static List<List<string>> SamplePairs(List<string> bbIds)
    // {
    //   var func2groups = new Dictionary<string, List<string>>();
    //   foreach (var bbId in bbIds)
    //   {
    //     var prefixIdx = bbId.IndexOf(".ll");
    //     var suffixIdx = bbId.IndexOf('.', prefixIdx + 3);
    //     var func = bbId.Substring(0, suffixIdx);

    //     if (func2groups.ContainsKey(func))
    //     {
    //       func2groups[func].Add(bbId);
    //     }
    //     else
    //     {
    //       func2groups[func] = new List<string> { bbId };
    //     }
    //   }

    //   var pairs = new List<List<string>>();
    //   var random = new Random(0);
    //   foreach (var groups in func2groups.Values)
    //   {
    //     var pars = new List<List<string>>();
    //     for (int i = 0; i < groups.Count - 1; i++)
    //     {
    //       for (int j = i + 1; j < groups.Count; j++)
    //       {
    //         pars.Add(new List<string> { groups[i], groups[j] });
    //       }
    //     }

    //     pars = pars.OrderBy(x => random.Next()).ToList();
    //     pars = pars.Take(Math.Min(20, pars.Count)).ToList();
    //     pairs.AddRange(pars);
    //   }

    //   // pairs = pairs.OrderBy(x => random.Next()).ToList();
    //   // pairs = pairs.Take(Math.Min(200, pairs.Count)).ToList();

    //   return pairs;
    // }

    // class BplItem
    // {
    //   public string id { get; set; }
    //   public string bpl { get; set; }
    // }

    // private static Dictionary<int, List<Tuple<int, string>>> BuildBplCodeDict(string bpl_code_dir, string funcs_file, string bb_id_map)
    // {
    //   List<FuncItem> funcs = File.ReadAllLines(funcs_file).Select(line => JsonSerializer.Deserialize<FuncItem>(line)).ToList();

    //   Dictionary<string, int> bb2id = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(bb_id_map));
    //   string[] bpl_files = Directory.GetFiles(bpl_code_dir, "*.jsonl", SearchOption.AllDirectories);
    //   Dictionary<int, string> bbid2bpl = new Dictionary<int, string>();
      
    //   Utils utils = new Utils();
    //   Program program = null;
      
    //   foreach (var file in bpl_files)
    //   {
    //     foreach (var line in File.ReadAllLines(file))
    //     {
    //       var item = JsonSerializer.Deserialize<BplItem>(line);
    //       if (bb2id.ContainsKey(item.id) && utils.ParseProgram(item.bpl, out program))
    //       {
    //         bbid2bpl[bb2id[item.id]] = item.bpl;
    //       }
    //     }
    //   }

    //   Dictionary<int, List<Tuple<int, string>>> funcid2bpls = new Dictionary<int, List<Tuple<int, string>>>();
    //   foreach (var func in funcs)
    //   {
    //     var bpls = func.blocks.Where(i => bbid2bpl.ContainsKey(i))
    //                           .Select(i => new Tuple<int, string>(i, bbid2bpl[i]))
    //                           .ToList();
    //     funcid2bpls[func.id] = bpls;
    //   }
    //   return funcid2bpls;
    // }

    private static void ComputeSemsimsForGroups(string func2bpls_file, string func_groups, 
      string temp_dir, int pair_per_group, int world_size, int rank)
    {
      Console.Out.WriteLine(string.Format("Rank {0}: Reading Bpl codes and groups info...", rank));

      List<Tuple<int, List<int>>> groups = 
        File.ReadAllLines(func_groups)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select((line, i) => new Tuple<int, List<int>>(i, line.Split(' ').Select(s => int.Parse(s)).ToList()))
            .ToList();

      // Drop groups that already done computing
      HashSet<int> done_indices = new HashSet<int>();
      foreach (var file in Directory.GetFiles(temp_dir, "group_done_*.txt", SearchOption.AllDirectories))
      {
        done_indices.UnionWith(File.ReadAllLines(file).Select(line => int.Parse(line.Trim())));
      }
      groups = groups.Where(tuple => !done_indices.Contains(tuple.Item1)).ToList();

      // Get sub groups to process
      int groupPerProcess = groups.Count / world_size;
      groups = new List<Tuple<int, List<int>>>(groups.GetRange(rank * groupPerProcess, Math.Min(groupPerProcess, groups.Count - rank * groupPerProcess)));

      // Read needed and legal bpls
      Utils utils = new Utils();
      Program program = null;
      var used_funcs = groups.SelectMany(group => group.Item2).ToHashSet();
      Dictionary<int, List<BplItem>> func2bpls = new Dictionary<int, List<BplItem>>();
      using (StreamReader reader = new StreamReader(func2bpls_file))
      {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
          FuncItem? funcItem = JsonSerializer.Deserialize<FuncItem>(line);
          if (used_funcs.Contains(funcItem.id))
          {
            func2bpls[funcItem.id] = funcItem.bpls.Where(item => utils.ParseProgram(item.bpl, out program)).ToList();
          }
        }
      }
        
      // Sample strategy
      Func<List<int>, List<Tuple<BplItem, BplItem>>> samplePairsFromGroup = group =>
      {
        var bpls = group.SelectMany(i => func2bpls[i]).ToList();
        var pairs = new List<Tuple<BplItem, BplItem>>();
        for (int i = 0; i < bpls.Count - 1; i++)
        {
          for (int j = i + 1; j < bpls.Count; j++)
          {
            pairs.Add(new Tuple<BplItem, BplItem>(bpls[i], bpls[j]));
          }
        }
        var random = new Random();
        pairs = pairs.OrderBy(x => random.Next()).ToList();
        pairs = pairs.Take(Math.Min(pair_per_group, pairs.Count)).ToList();

        return pairs;
      };

      string res_file = Path.Join(temp_dir, "semsims_" + rank.ToString() + ".txt");
      string prog_file = Path.Join(temp_dir, "group_done_" + rank.ToString() + ".txt");

      Console.Out.WriteLine(string.Format("Rank {0}: Computing semsim for groups from {1} to {2}...",
                                          rank,    
                                          groups.First().Item1,
                                          groups.Last().Item1));
      
      int done = 0;
      DateTime beg_time = DateTime.Now;

      foreach (var group in groups)
      {
        string res_str = "";
        var pairs = samplePairsFromGroup(group.Item2);
        foreach (var pair in pairs)
        {
          float sim = BplMatch.RunMatch(pair.Item1.bpl, pair.Item2.bpl);
          res_str += string.Format("{0} {1} {2}\n", pair.Item1.id, pair.Item2.id, sim);
        }

        using (StreamWriter writer = new StreamWriter(res_file, true))
        {
          writer.Write(res_str);
        }

        using (StreamWriter writer = new StreamWriter(prog_file, true))
        {
          writer.WriteLine(group.Item1);
        }

        done += 1;
        TimeSpan elapsed = DateTime.Now - beg_time;
        long avg_time = (long)(elapsed.TotalSeconds / done);
        TimeSpan eta = TimeSpan.FromSeconds(avg_time*(groups.Count - done));
        string log_str = string.Format("Rank {0}: {1,8}/{2} groups {3} s/group {4:dd\\:hh\\:mm\\:ss} elapsed {5:dd\\:hh\\:mm\\:ss} ETA\n",
          rank, done, groups.Count, avg_time, elapsed, eta);
        
        Console.Out.Write(log_str);
      }
    }
    class BplItem
    {
      public int id { get; set; }
      public string bpl { get; set; }
    }

    class FuncItem
    {
      public int id { get; set; }
      public List<BplItem> bpls { get; set; }
    }
  }
}