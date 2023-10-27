using Microsoft.Boogie;
using System.Diagnostics;
using Type = Microsoft.Boogie.Type;

namespace SemSim
{
  public class Utils
  {
    public static readonly int MaxAsserts = 10000;

    private static ExecutionEngine Engine;
    private static CommandLineOptions PrintOptions;
    private static CommandLineOptions ProcessOptions;
    private static StringWriter PrintBuffer;
    private static StringWriter ProcessBuffer;

    static Utils()
    {
      PrintBuffer = new StringWriter();
      ProcessBuffer = new StringWriter();

      var printBoogieOptions = $"/typeEncoding:m -timeLimit:1 -removeEmptyBlocks:0 /printInstrumented";
      PrintOptions = CommandLineOptions.FromArguments(TextWriter.Null, printBoogieOptions.Split(' '));
      PrintOptions.UseUnsatCoreForContractInfer = true;
      PrintOptions.ContractInfer = true;
      PrintOptions.ExplainHoudini = true;

      var processBoogieOptions = $"/timeLimit:1 /errorLimit:{MaxAsserts} /useArrayAxioms";
      ProcessOptions = CommandLineOptions.FromArguments(TextWriter.Null, processBoogieOptions.Split(' '));

      Engine = ExecutionEngine.CreateWithoutSharedCache(ProcessOptions);
    }

    public static bool ParseProgram(string text, out Program program)
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

      errCount = program.Resolve(PrintOptions);
      if (errCount > 0)
      {
        Debug.WriteLine($"Name resolution errors in: {text}");
        return false;
      }

      ModSetCollector c = new ModSetCollector(PrintOptions);
      c.DoModSetAnalysis(program);

      return true;
    }

    public static string PrintProgram(Program program)
    {
      var ttw = new TokenTextWriter(ProcessBuffer, PrintOptions);
      program.Emit(ttw);

      string res = ProcessBuffer.ToString();
      ProcessBuffer.GetStringBuilder().Clear();
      return res;
    }

    public static string? RunBoogie(Program program)
    {
      var success = Engine.ProcessProgram(ProcessBuffer, program, "from_string").Result;

      if (!success)
      {
        Debug.WriteLine($"Boogie running errors due to: {ProcessBuffer}");
      }

      string? res = success ? ProcessBuffer.ToString() : null;
      ProcessBuffer.GetStringBuilder().Clear();
      return res;
    }

    public class VarRenamer : StandardVisitor
    {
      private readonly string _prefix;
      public List<string> Ignore;
      public VarRenamer(string prefix, string[] ignore)
      {
        _prefix = prefix;
        Ignore = new List<string>(ignore);
      }



      public override Implementation VisitImplementation(Implementation node)
      {
        var result = base.VisitImplementation(node);
        result.Name = _prefix + result.Name;
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
        if (node is GlobalVariable || node is Constant || node.Name.StartsWith(_prefix))
          return node;
        var result = node.Clone() as Variable;
        if (result == null)
          return node;
        result.Name = _prefix + result.Name;
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
        node.Label = _prefix + node.Label;
        var gotoCmd = node.TransferCmd as GotoCmd;
        if (gotoCmd != null)
          node.TransferCmd = new GotoCmd(Token.NoToken, gotoCmd.labelNames.Select(l => _prefix + l).ToList());
        return base.VisitBlock(node);
      }
    }

    public static Type GetExprType(Expr expr)
    {
      var le = expr as LiteralExpr;
      if (le != null)
        return le.Type;
      var ie = expr as IdentifierExpr;
      if (ie != null)
        return ie.Decl.TypedIdent.Type;
      var ne = expr as NAryExpr;
      if (ne != null && ne.Fun is MapSelect)
        return ((ne.Args[0] as IdentifierExpr).Decl.TypedIdent.Type as MapType).Result;
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
