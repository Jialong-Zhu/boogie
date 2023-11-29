using System.Formats.Asn1;
using System.Security;
using System.Text.Json;


namespace SemSim
{
  public class Semsim
  {
    private static void Usage()
    {
      Console.WriteLine("semsim - Compute semantic similarity between bpl functions.");
      Console.WriteLine("Usage: semsim <query.bpl> <target.bpl>");
      Console.WriteLine("    or semsim <bpl_code.jsonl> <semsims.txt>");
    }

    static int Main(string[] args)
    {
      if (args[0].EndsWith(".bpl"))
      {
        using (StreamReader qr = new StreamReader(args[0]), tr = new StreamReader(args[1]))
        {
          string qtext = qr.ReadToEnd();
          string ttext = tr.ReadToEnd();
          float sim = BplMatch.RunMatch(qtext, ttext);
          Console.Out.WriteLine($"Sim: {sim}");
        }
      }
      else if (args[0].EndsWith(".jsonl"))
      {
        ComputeSemsims(args[0], args[1]);
      }
      else
      {
        Usage();
        return -1;
      }

      return 0;
    }

    private static void ComputeSemsims(string bpl_code_file, string semsims_file)
    {
      Dictionary<string, string> id2bpl = new Dictionary<string, string>();
      try
      {
        using (StreamReader reader = new StreamReader(bpl_code_file))
        {
          string? line;
          while ((line = reader.ReadLine()) != null)
          {
            BplItem? item = JsonSerializer.Deserialize<BplItem>(line);
            if (item != null)
            {
              id2bpl[item.id] = item.bpl;
            }
          }
        }
      }
      catch (Exception e)
      {
        Console.WriteLine($"Error reading bpl code file: {e.Message}");
      }

      List<List<string>> pairs = SamplePairs(id2bpl.Keys.ToList());

      using (StreamWriter writer = new StreamWriter(semsims_file))
      {
        for (int i = 0; i < pairs.Count; ++i)
        {
          float sim = BplMatch.RunMatch(id2bpl[pairs[i][0]], id2bpl[pairs[i][1]]);
          writer.WriteLine(String.Format("{0} {1} {2}", pairs[i][0], pairs[i][1], sim));
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
      var random = new Random();
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
  }
}