using System.Text.RegularExpressions;
using Microsoft.Boogie;
using BType = Microsoft.Boogie.Type;
using System.Diagnostics;


namespace SemSim
{
  public class BplMatch
  {
    private Utils.VarRenamer _renamer;
    private int _eqVarsCounter;
    private int _labelCounter;

    private readonly Variable _havocVar;

    // important: these should not appear in the input programs
    private static readonly string HavocVarName = "_havoc";
    private static readonly string AssumeVarPrefix = "_eq";
    private static readonly string SectionLabelPrefix = "_section";
    private static readonly string TargetSuffix = ".target";

    private static readonly float ErrorSim = -1;

    private static void Usage()
    {
      Console.WriteLine("bplmatch - find the best matching for given query and target.");
      Console.WriteLine("Usage: bplmatch <query.bpl> <target.bpl>");
      Console.WriteLine("-break - start in debug mode");
    }

    static int Main(string[] args)
    {

      if (args.Length < 2 || args.Length > 3)
      {
        Usage();
        return -1;
      }

      if (args.Length == 3 && args[2].ToLower() == "-break")
      {
        Debugger.Launch();
      }

      string qtext, ttext;
      if (File.Exists(args[0]) && File.Exists(args[1]))
      {
        using StreamReader qr = new StreamReader(args[0]), tr = new StreamReader(args[1]);
        qtext = qr.ReadToEnd();
        ttext = tr.ReadToEnd();
      }
      else
      {
        qtext = args[0];
        ttext = args[1];
      }

      float sim = RunMatch(qtext, ttext);
      Console.Out.WriteLine($"Sim: {sim}");

      return 0;
    }

    public static float RunMatch(string queryText, string targetText)
    {
      return (new BplMatch()).Run(queryText, targetText);
    }

    public BplMatch()
    {
      _renamer = new Utils.VarRenamer(TargetSuffix, new string[] { });
      _eqVarsCounter = 0;
      _havocVar = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, HavocVarName, BType.Bool));
    }

    private float Run(string queryText, string targetText)
    {
      Program queryProgram, targetProgram;
      if (!Utils.ParseProgram(queryText, out queryProgram) || !Utils.ParseProgram(targetText, out targetProgram))
      {
        return ErrorSim;
      }

      if (queryProgram.Implementations.Count() != 1 || targetProgram.Implementations.Count() != 1)
      {
        Debug.WriteLine("One Implementation per program, please.");
        return ErrorSim;
      }

      Program joinedProgram = JoinTopLevelDeclarations(queryProgram, targetProgram);

      var joinedImplementation = joinedProgram.Implementations.Single();
      var targetImplementation = targetProgram.Implementations.Single();

      targetImplementation = _renamer.VisitImplementation(targetImplementation);
      
      int numTotalLocals = joinedImplementation.LocVars.Count + targetImplementation.LocVars.Count;

      int numAssert;
      var assumeVars = new List<Tuple<Variable, Expr, Expr>>();
      var blocks = CreateAssertsBlocks(joinedImplementation, targetImplementation, assumeVars, out numAssert);

      if (numAssert > Utils.MaxAsserts)
      {
        Debug.WriteLine($"Max asserts exceeded: {numAssert} > {Utils.MaxAsserts}");
        return ErrorSim;
      }

      JoinImplementations(joinedImplementation, targetImplementation);

      // check if both program contains memory map, if so assume they are equal
      var memVars = joinedProgram.GlobalVariables.FindAll(v => v.ToString().StartsWith("$Mem."));
      var targetMemVars = memVars.Where(v => v.ToString().EndsWith(TargetSuffix));
      var queryMemVars = memVars.Except(targetMemVars);

      targetMemVars.ForEach(tv => {
        var sameMaps = queryMemVars.Where(qv => tv.ToString().StartsWith(qv.ToString())).ToList();
        if (sameMaps.Count == 1) {
          joinedImplementation.Blocks.First().Cmds.Insert(0, new AssumeCmd(Token.NoToken, Expr.Eq(
            Expr.Ident(tv), Expr.Ident(sameMaps.Single()))));
        }
      });

      joinedImplementation.Blocks.Last().TransferCmd = new GotoCmd(Token.NoToken, blocks);
      joinedImplementation.Blocks.AddRange(blocks);

      // print new program and reparse to fix resolving problems
      string joinedText = Utils.PrintProgram(joinedProgram);
      Debug.WriteLine(joinedText);
      if (!Utils.ParseProgram(joinedText, out joinedProgram))
      {
        return ErrorSim;
      }

      // run Boogie and get the output
      var output = Utils.RunBoogie(joinedProgram);
      Debug.WriteLine(output);
      if (output == null)
      {
        return ErrorSim;
      }

      // reparse queryProgram from joinedText to avoid side effects from RunBoogie
      if (!Utils.ParseProgram(joinedText, out joinedProgram))
      {
        return ErrorSim;
      }

      // find all the failed asserts 
      var failedAssertsLineNumbers = new HashSet<int>();
      var match = Regex.Match(output, @"\(([0-9]+),[0-9]+\): Error: this assertion could not be proved");
      while (match.Success)
      {
        failedAssertsLineNumbers.Add(int.Parse(match.Groups[1].Value));
        match = match.NextMatch();
      }

      // map blocks to true asserts
      var trueAsserts = new Dictionary<Block, HashSet<AssertCmd>>();
      joinedProgram.Implementations.Single().Blocks.ForEach(b =>
      {
        trueAsserts[b] = new HashSet<AssertCmd>();
        b.Cmds.ForEach(c =>
        {
          var ac = c as AssertCmd;
          if (ac != null && !failedAssertsLineNumbers.Contains(ac.Line)) {
            trueAsserts[b].Add(ac);
          }
        });
      });

      // extract the assert expressions and add in the assume expressions
      var bestBlock = trueAsserts.Keys.First(b => trueAsserts[b].Count == trueAsserts.Max(p => p.Value.Count));
      bestBlock.Cmds.ForEach(c =>
      {
        var ac = c as AssumeCmd;
        if (ac != null)
        {
          assumeVars.ForEach(t =>
          {
            if (t.Item1.ToString() == ac.Expr.ToString())
            {
              Debug.WriteLine(t.Item2 + " == " + t.Item3);
            }
          });

        }
      });
      Debug.WriteLine("==> ");

      bool error = false;
      var matchedVars = new HashSet<string>(); // remember that a variable can be matched more than once
      trueAsserts[bestBlock].ForEach(a =>
      {
        var s = a.Expr.ToString().Split(new string[] { " || " }, StringSplitOptions.None);
        if (s.Count() <= 1)
        {
          error = true;
          Debug.WriteLine($"Too few exprs from {a.Expr}");
        }
        else
        {
          Debug.WriteLine(s[1]);
          var vars = s[1].Split(new string[] { " == " }, StringSplitOptions.None);
          matchedVars.Add(vars[0]);
          matchedVars.Add(vars[1]);
        }
      });
      if (error)
      {
        return ErrorSim;
      }

      float res = matchedVars.Count / (float)numTotalLocals;
      Debug.WriteLine($"\nMatched sim: {res}");
      return res;
    }

    private void JoinImplementations(Implementation queryImplementation, Implementation targetImplementation)
    {
      queryImplementation.InParams.AddRange(targetImplementation.InParams);
      queryImplementation.Proc.InParams.AddRange(targetImplementation.InParams);
      queryImplementation.LocVars.AddRange(targetImplementation.LocVars);
      queryImplementation.LocVars.Add(_havocVar);
      queryImplementation.Blocks.Last().TransferCmd = new GotoCmd(Token.NoToken,
          new List<Block>() { targetImplementation.Blocks.First() });
      queryImplementation.Blocks.AddRange(targetImplementation.Blocks);
    }

    private Program JoinTopLevelDeclarations(Program queryProgram, Program targetProgram)
    {
      Program joinedProgram = new Program();

      // add all of target's functions, constants, globals and typeDecls
      joinedProgram.AddTopLevelDeclarations(targetProgram.TopLevelDeclarations
        .Where(node => node is GlobalVariable || node is Constant || 
                       node is Function || node is TypeSynonymDecl ||
                       node is TypeCtorDecl)
        .Select(decl => _renamer.VisitDeclaration(decl)));

      var strings = joinedProgram.TopLevelDeclarations.Select(v => v.ToString()).ToList();
      joinedProgram.AddTopLevelDeclarations(queryProgram.TopLevelDeclarations
        .Where(decl1 => !strings.Contains(decl1.ToString())));

      joinedProgram.Procedures.Single().Modifies.AddRange(targetProgram.Procedures.Single().Modifies.Select(v => new IdentifierExpr(Token.NoToken, v.Name + TargetSuffix)));

      return joinedProgram;
    }

    private List<Expr> CreateAssertsExprs(Implementation queryImplementation, Implementation targetImplementation)
    {
      var result = new List<Expr>();
      queryImplementation.LocVars.ForEach(v =>
      {
        var type = Utils.GetExprType(Expr.Ident(v));
        if (type != null) {
          targetImplementation.LocVars.ForEach(v2 => {
            if (type.Equals(Utils.GetExprType(Expr.Ident(v2)))) { result.Add(Expr.Eq(Expr.Ident(v), Expr.Ident(v2))); }
          });
        }
      });
      return result;
    }

    private List<Block> CreateAssertsBlocks(Implementation queryImplementation, Implementation targetImplementation, List<Tuple<Variable, Expr, Expr>> assumeVars, out int numAsserts)
    {
      var exprs = CreateAssertsExprs(queryImplementation, targetImplementation);
      var assertsCmds = CreateAsserts(exprs);
      numAsserts = assertsCmds.Count;

      queryImplementation.InParams.ForEach(iq =>
      {
        targetImplementation.InParams.ForEach(it =>
        {
          if (iq.TypedIdent.Type.Equals(it.TypedIdent.Type))
          {
            var eqVar = new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, AssumeVarPrefix + "_" + _eqVarsCounter++, BType.Bool));
            assumeVars.Add(new Tuple<Variable, Expr, Expr>(eqVar, Expr.Ident(iq), Expr.Ident(it)));
            queryImplementation.Blocks[0].Cmds.Insert(0, Utils.CreateAssignCmd(new List<IdentifierExpr>() { Expr.Ident(eqVar) }, new List<Expr>() { Expr.Eq(Expr.Ident(iq), Expr.Ident(it)) }));
          }
        });
      });

      // The equality vars are grouped according to lhs. This means that expressions like eq_0 = `rax == v2.rcx` and eq_13 = `rax == v2.p0` will be 
      // grouped together, to make the assumes choosing algorithm more efficient (we won't select eq_0 and eq_13 for the same section)
      var eqVarsGroups = new Dictionary<string, List<Tuple<Variable, Expr, Expr>>>();
      assumeVars.ForEach(t =>
      {
        var lhs = t.Item2.ToString();
        if (!eqVarsGroups.ContainsKey(lhs)) {
          eqVarsGroups[lhs] = new List<Tuple<Variable, Expr, Expr>>();
        }
        eqVarsGroups[lhs].Add(t);
      });
      assumeVars.ForEach(t => queryImplementation.LocVars.Add(t.Item1));
      return CreateAssertsWithAssumes(eqVarsGroups.Values.ToList(), assertsCmds);
    }

    private List<Block> CreateAssertsWithAssumes(List<List<Tuple<Variable, Expr, Expr>>> eqVarsGroups, List<Cmd> asserts)
    {
      int n = eqVarsGroups.Count();
      if (n == 0)
      {
        var b = new Block { Label = SectionLabelPrefix + "_" + _labelCounter++ };
        b.Cmds.AddRange(asserts);
        return new List<Block>() { b };
      }

      var result = new List<Block>();
      EnumerateAssumes(0, new List<Variable>(), new HashSet<int>(), new HashSet<string>(), eqVarsGroups, asserts, result);
      return result;
    }

    private List<Cmd> CreateAsserts(IEnumerable<Expr> exprs)
    {
      var result = new List<Cmd>();
      foreach (var e in exprs)
      {
        result.Add(new HavocCmd(Token.NoToken, new List<IdentifierExpr>() { Expr.Ident(_havocVar) }));
        result.Add(new AssertCmd(Token.NoToken, Expr.Or(Expr.Ident(_havocVar), e)));
      }
      // if the tracelets have conflicting assumes, everything can be proved.
      // add an 'assert false;' at the end, to check at this did not happen
      result.Add(new AssertCmd(Token.NoToken, Expr.False));
      return result.ToList();
    }

    private void EnumerateAssumes(int group, List<Variable> eqVarsPick, HashSet<int> usedLhs, HashSet<string> usedRhs, 
      List<List<Tuple<Variable, Expr, Expr>>> eqVarsGroups, List<Cmd> asserts, List<Block> result)
    {
      if (group == eqVarsGroups.Count) 
      {
        // enumeration ends, check if it's a maximal match
        for (int i = 0; i < eqVarsGroups.Count; ++i) 
        {
          if (usedLhs.Contains(i)) {
            continue;
          }
          for (int j = 0; j < eqVarsGroups[i].Count; ++j)
          { 
            if(!usedRhs.Contains(eqVarsGroups[i][j].Item3.ToString())) {
              return;
            }
          }
        }

        var b = new Block { Label = SectionLabelPrefix + "_" + _labelCounter++ };
        foreach (var eqv in eqVarsPick) {
          b.Cmds.Add(new AssumeCmd(Token.NoToken, Expr.Ident(eqv)));
        }
        b.Cmds.AddRange(asserts);
        b.TransferCmd = new ReturnCmd(Token.NoToken);
        result.Add(b);
        return;
      }

      // try all possible edges for group
      for (int j = 0; j < eqVarsGroups[group].Count; ++j)
      {
        if (usedRhs.Contains(eqVarsGroups[group][j].Item3.ToString())) {
          continue;
        }

        var newEqVarsPick = new List<Variable>(eqVarsPick) { eqVarsGroups[group][j].Item1};
        var newUsedLhs = new HashSet<int>(usedLhs) {group};
        var newUsedRhs = new HashSet<string>(usedRhs) {eqVarsGroups[group][j].Item3.ToString()};
        EnumerateAssumes(group + 1, newEqVarsPick, newUsedLhs, newUsedRhs, eqVarsGroups, asserts, result);
      }

      // try not select edge for group
      EnumerateAssumes(group + 1, eqVarsPick, usedLhs, usedRhs, eqVarsGroups, asserts, result);
    }
  }

}
