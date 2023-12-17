using Microsoft.Boogie;
using System.Diagnostics;

namespace SemSim
{
  public class Utils
  {
    public static readonly int MaxAsserts = 1000;

    private ExecutionEngine engine;
    private CommandLineOptions printOptions;
    private CommandLineOptions processOptions;
    private StringWriter processBuffer;

    public Utils()
    {
      processBuffer = new StringWriter();

      var printBoogieOptions = $"/typeEncoding:m -timeLimit:1 -removeEmptyBlocks:0 /printInstrumented";
      printOptions = CommandLineOptions.FromArguments(TextWriter.Null, printBoogieOptions.Split(' '));
      printOptions.UseUnsatCoreForContractInfer = true;
      printOptions.ContractInfer = true;
      printOptions.ExplainHoudini = true;

      var processBoogieOptions = $"/timeLimit:1 /errorLimit:{MaxAsserts} /useArrayAxioms";
      processOptions = CommandLineOptions.FromArguments(TextWriter.Null, processBoogieOptions.Split(' '));

      engine = ExecutionEngine.CreateWithoutSharedCache(processOptions);
    }

    public bool ParseProgram(string text, out Program program)
    {
      program = null;
      int errCount;
      try
      {
        errCount = Parser.Parse(text, "from_string", out program);
        if (errCount != 0 || program == null)
        {
          Debug.WriteLine($"Parse errors detected in: {text}");
          return false;
        }
      }
      catch (Exception e)
      {
        Debug.WriteLine($"Exception parsing program: {e.Message}");
        return false;
      }

      errCount = program.Resolve(printOptions);
      if (errCount > 0)
      {
        Debug.WriteLine($"Name resolution errors in: {text}");
        return false;
      }

      ModSetCollector c = new ModSetCollector(printOptions);
      c.DoModSetAnalysis(program);

      return true;
    }

    public string PrintProgram(Program program)
    {
      var ttw = new TokenTextWriter(processBuffer, printOptions);
      program.Emit(ttw);

      string res = processBuffer.ToString();
      processBuffer.GetStringBuilder().Clear();
      return res;
    }

    public string? RunBoogie(Program program)
    {
      var result = engine.ProcessProgram(processBuffer, program, "from_string");
      var success = result.Result;

      if (!success)
      {
        Debug.WriteLine($"Boogie running errors due to: {processBuffer}");
      }

      string? res = success ? processBuffer.ToString() : null;
      processBuffer.GetStringBuilder().Clear();
      return res;
    }

    public class VarRenamer : StandardVisitor
    {
      private readonly string _suffix;
      public List<string> Ignore;
      public VarRenamer(string suffix, string[] ignore)
      {
        _suffix = suffix;
        Ignore = new List<string>(ignore);
      }

      public override Declaration VisitDeclaration(Declaration node)
      {
        if (node is GlobalVariable) {
          GlobalVariable gv = (GlobalVariable)node;
          return new GlobalVariable(Token.NoToken, new TypedIdent(Token.NoToken, gv.Name + _suffix, gv.TypedIdent.Type));
        } else if (node is Constant) {
          Constant ct = (Constant)node;
          return new Constant(Token.NoToken, new TypedIdent(Token.NoToken, ct.Name + _suffix, ct.TypedIdent.Type));       
        }

        return node;
      }

      public override Implementation VisitImplementation(Implementation node)
      {
        var result = base.VisitImplementation(node);
        // result.Name = result.Name + _suffix;
        result.InParams = result.InParams.Select(i => new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, i.Name, i.TypedIdent.Type)) as Variable).ToList();
        result.OutParams = result.OutParams.Select(o => new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, o.Name, o.TypedIdent.Type)) as Variable).ToList();
        result.LocVars = result.LocVars.Select(v => new LocalVariable(Token.NoToken, new TypedIdent(Token.NoToken, v.Name, v.TypedIdent.Type)) as Variable).ToList();
        return result;
      }

      public override Expr VisitNAryExpr(NAryExpr node)
      {
        node.Args = node.Args.Select(arg => VisitExpr(arg)).ToList();
        return node;
      }

      public override Variable VisitVariable(Variable node)
      {
        // if (node.Name.StartsWith(_prefix)) {
        //   return node;
        // }
        var result = node.Clone() as Variable;
        if (result == null) {
          return node;
        }
        result.Name = result.Name + _suffix;
        return result;
      }

      public override Expr VisitIdentifierExpr(IdentifierExpr node)
      {
        var result = new IdentifierExpr(Token.NoToken, VisitVariable(node.Decl));
        return result;
      }

      // public override Cmd VisitCallCmd(CallCmd node)
      // {
      //   if (!Ignore.Any(p => node.callee.StartsWith(p)))
      //     node.callee = _prefix + node.callee;
      //   return base.VisitCallCmd(node);
      // }

      public override Cmd VisitAssignCmd(AssignCmd node)
      {
        var renamedLhss = new List<AssignLhs>();
        foreach (var l in node.Lhss)
        {
          if (l is SimpleAssignLhs)
          {
            renamedLhss.Add(new SimpleAssignLhs(Token.NoToken, Expr.Ident(VisitVariable(l.DeepAssignedVariable))));
          }
          else if (l is MapAssignLhs)
          {
            var mal = l as MapAssignLhs;
            renamedLhss.Add(new MapAssignLhs(Token.NoToken,
                new SimpleAssignLhs(Token.NoToken, VisitIdentifierExpr(mal.DeepAssignedIdentifier) as IdentifierExpr),
                mal.Indexes.Select(i => VisitExpr(i)).ToList()));
          }
          else
          {
            Debug.WriteLine($"Unknown assign type: {l.GetType()}");
          }
        }

        var renamedRhss = new List<Expr>();
        node.Rhss.ForEach(r => renamedRhss.Add(VisitExpr(r)));

        var result = new AssignCmd(Token.NoToken, renamedLhss, renamedRhss);
        return result;
      }

      public override Block VisitBlock(Block node)
      {
        node.Label = node.Label + _suffix;
        var gotoCmd = node.TransferCmd as GotoCmd;
        if (gotoCmd != null) {
          node.TransferCmd = new GotoCmd(Token.NoToken, gotoCmd.labelNames.Select(l => l + _suffix).ToList());
        }
        return base.VisitBlock(node);
      }
    }

    public static string? GetExprType(Expr expr)
    {
      var le = expr as LiteralExpr;
      if (le != null) {
        return le.Type.ToString();
      }
      var ie = expr as IdentifierExpr;
      if (ie != null) {
        return ie.Decl.TypedIdent.Type.ToString();
      }
      var ne = expr as NAryExpr;
      if (ne != null && ne.Fun is MapSelect) {
        return ((ne.Args[0] as IdentifierExpr).Decl.TypedIdent.Type as MapType).Result.ToString();
      }
      return null;
    }
    
    public static AssignCmd CreateAssignCmd(IEnumerable<IdentifierExpr> lhs, IEnumerable<Expr> rhs)
    {
      List<AssignLhs> assignLhss = new List<AssignLhs>();
      lhs.ForEach(i => assignLhss.Add(new SimpleAssignLhs(Token.NoToken, i)));
      return new AssignCmd(new Token(), assignLhss, rhs.ToList());
    }
  }
}
