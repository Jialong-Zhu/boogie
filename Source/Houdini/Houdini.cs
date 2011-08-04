//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Diagnostics.Contracts;
using System.Collections.Generic;
using Microsoft.Boogie;
using Microsoft.Boogie.Simplify;
using VC;
using Microsoft.Boogie.Z3;
using System.Collections;
using System.IO;
using Microsoft.AbstractInterpretationFramework;
using Graphing;

namespace Microsoft.Boogie.Houdini {

  class ReadOnlyDictionary<K, V> {
    private Dictionary<K, V> dictionary;
    public ReadOnlyDictionary(Dictionary<K, V> dictionary) {
      this.dictionary = dictionary;
    }

    public Dictionary<K, V>.KeyCollection Keys {
      get { return this.dictionary.Keys; }
    }

    public bool TryGetValue(K k, out V v) {
      return this.dictionary.TryGetValue(k, out v);
    }

    public bool ContainsKey(K k) {
      return this.dictionary.ContainsKey(k);
    }
  }

  public abstract class HoudiniObserver {
    public virtual void UpdateStart(Program program, int numConstants) { }
    public virtual void UpdateIteration() { }
    public virtual void UpdateImplementation(Implementation implementation) { }
    public virtual void UpdateAssignment(Dictionary<string, bool> assignment) { }
    public virtual void UpdateOutcome(VCGen.Outcome outcome) { }
    public virtual void UpdateEnqueue(Implementation implementation) { }
    public virtual void UpdateDequeue() { }
    public virtual void UpdateConstant(string constantName) { }
    public virtual void UpdateEnd(bool isNormalEnd) { }
    public virtual void UpdateFlushStart() { }
    public virtual void UpdateFlushFinish() { }
    public virtual void SeeException(string msg) { }
  }

  public class IterationTimer<K> {
    private Dictionary<K, List<double>> times;

    public IterationTimer() {
      times = new Dictionary<K, List<double>>();
    }

    public void AddTime(K key, double timeMS) {
      List<double> oldList;
      times.TryGetValue(key, out oldList);
      if (oldList == null) {
        oldList = new List<double>();
      }
      else {
        times.Remove(key);
      }
      oldList.Add(timeMS);
      times.Add(key, oldList);
    }

    public void PrintTimes(TextWriter wr) {
      wr.WriteLine("Total procedures: {0}", times.Count);
      double total = 0;
      int totalIters = 0;
      foreach (KeyValuePair<K, List<double>> kv in times) {
        int curIter = 0;
        wr.WriteLine("Times for {0}:", kv.Key);
        foreach (double v in kv.Value) {
          wr.WriteLine("  ({0})\t{1}ms", curIter, v);
          total += v;
          curIter++;
        }
        totalIters += curIter;
      }
      total = total / 1000.0;
      wr.WriteLine("Total time: {0} (s)", total);
      wr.WriteLine("Avg: {0} (s/iter)", total / totalIters);
    }
  }

  public class HoudiniTimer : HoudiniObserver {
    private DateTime startT;
    private Implementation curImp;
    private IterationTimer<string> times;
    private TextWriter wr;

    public HoudiniTimer(TextWriter wr) {
      this.wr = wr;
      times = new IterationTimer<string>();
    }
    public override void UpdateIteration() {
      startT = DateTime.Now;
    }
    public override void UpdateImplementation(Implementation implementation) {
      curImp = implementation;
    }
    public override void UpdateOutcome(VCGen.Outcome o) {
      Contract.Assert(curImp != null);
      DateTime endT = DateTime.Now;
      times.AddTime(curImp.Name, (endT - startT).TotalMilliseconds); // assuming names are unique
    }
    public void PrintTimes() {
      wr.WriteLine("-----------------------------------------");
      wr.WriteLine("Times for each iteration for each procedure");
      wr.WriteLine("-----------------------------------------");
      times.PrintTimes(wr);
    }
  }

  public class HoudiniTextReporter : HoudiniObserver {
    private TextWriter wr;
    private int currentIteration = -1;

    public HoudiniTextReporter(TextWriter wr) {
      this.wr = wr;
    }
    public override void UpdateStart(Program program, int numConstants) {
      wr.WriteLine("Houdini started:" + program.ToString() + " #constants: " + numConstants.ToString());
      currentIteration = -1;
      wr.Flush();
    }
    public override void UpdateIteration() {
      currentIteration++;
      wr.WriteLine("---------------------------------------");
      wr.WriteLine("Houdini iteration #" + currentIteration);
      wr.Flush();
    }
    public override void UpdateImplementation(Implementation implementation) {
      wr.WriteLine("implementation under analysis :" + implementation.Name);
      wr.Flush();
    }
    public override void UpdateAssignment(Dictionary<string, bool> assignment) {
      bool firstTime = true;
      wr.Write("assignment under analysis : axiom (");
      foreach (KeyValuePair<string, bool> kv in assignment) {
        if (!firstTime) wr.Write(" && "); else firstTime = false;
        string valString; // ugliness to get it lower cased
        if (kv.Value) valString = "true"; else valString = "false";
        wr.Write(kv.Key + " == " + valString);
      }
      wr.WriteLine(");");
      wr.Flush();
    }
    public override void UpdateOutcome(VCGen.Outcome outcome) {
      wr.WriteLine("analysis outcome :" + outcome);
      wr.Flush();
    }
    public override void UpdateEnqueue(Implementation implementation) {
      wr.WriteLine("worklist enqueue :" + implementation.Name);
      wr.Flush();
    }
    public override void UpdateDequeue() {
      wr.WriteLine("worklist dequeue");
      wr.Flush();
    }
    public override void UpdateConstant(string constantName) {
      wr.WriteLine("constant disabled : " + constantName);
      wr.Flush();
    }
    public override void UpdateEnd(bool isNormalEnd) {
      wr.WriteLine("Houdini ended: " + (isNormalEnd ? "Normal" : "Abnormal"));
      wr.WriteLine("Number of iterations: " + (this.currentIteration + 1));
      wr.Flush();
    }
    public override void UpdateFlushStart() {
      wr.WriteLine("***************************************");
      wr.WriteLine("Flushing remaining implementations");
      wr.Flush();
    }
    public override void UpdateFlushFinish() {
      wr.WriteLine("***************************************");
      wr.WriteLine("Flushing finished");
      wr.Flush();
    }
    public override void SeeException(string msg) {
      wr.WriteLine("Caught exception: " + msg);
      wr.Flush();
    }

  }


  public abstract class ObservableHoudini {
    private List<HoudiniObserver> observers = new List<HoudiniObserver>();

    public void AddObserver(HoudiniObserver observer) {
      if (!observers.Contains(observer))
        observers.Add(observer);
    }
    private delegate void NotifyDelegate(HoudiniObserver observer);

    private void Notify(NotifyDelegate notifyDelegate) {
      foreach (HoudiniObserver observer in observers) {
        notifyDelegate(observer);
      }
    }
    protected void NotifyStart(Program program, int numConstants) {
      NotifyDelegate notifyDelegate = (NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateStart(program, numConstants); };
      Notify(notifyDelegate);
    }
    protected void NotifyIteration() {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateIteration(); });
    }
    protected void NotifyImplementation(Implementation implementation) {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateImplementation(implementation); });
    }
    protected void NotifyAssignment(Dictionary<string, bool> assignment) {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateAssignment(assignment); });
    }
    protected void NotifyOutcome(VCGen.Outcome outcome) {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateOutcome(outcome); });
    }
    protected void NotifyEnqueue(Implementation implementation) {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateEnqueue(implementation); });
    }
    protected void NotifyDequeue() {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateDequeue(); });
    }
    protected void NotifyConstant(string constantName) {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateConstant(constantName); });
    }
    protected void NotifyEnd(bool isNormalEnd) {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateEnd(isNormalEnd); });
    }
    protected void NotifyFlushStart() {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateFlushStart(); });
    }
    protected void NotifyFlushFinish() {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.UpdateFlushFinish(); });
    }

    protected void NotifyException(string msg) {
      Notify((NotifyDelegate)delegate(HoudiniObserver r) { r.SeeException(msg); });
    }
  }

  public class Houdini : ObservableHoudini {
    private Program program;
    private ReadOnlyDictionary<string, IdentifierExpr> houdiniConstants;
    private ReadOnlyDictionary<Implementation, HoudiniVCGen> vcgenSessions;
    private Graph<Implementation> callGraph;
    private bool continueAtError;

    public Houdini(Program program, bool continueAtError) {
      this.program = program;
      this.callGraph = BuildCallGraph(program);
      this.continueAtError = continueAtError;
      this.houdiniConstants = CollectExistentialConstants(program);
      this.vcgenSessions = PrepareVCGenSessions(program);
    }

    private ReadOnlyDictionary<Implementation, HoudiniVCGen> PrepareVCGenSessions(Program program) {
      Dictionary<Implementation, HoudiniVCGen> vcgenSessions = new Dictionary<Implementation, HoudiniVCGen>();
      foreach (Declaration decl in program.TopLevelDeclarations) {
        Implementation impl = decl as Implementation;
        if (impl != null) {
          // make a different simplify log file for each function
          String simplifyLog = null;
          if (CommandLineOptions.Clo.SimplifyLogFilePath != null) {
            simplifyLog = impl.ToString() + CommandLineOptions.Clo.SimplifyLogFilePath;
          }
          HoudiniVCGen vcgen = new HoudiniVCGen(program, impl, simplifyLog, CommandLineOptions.Clo.SimplifyLogFileAppend);
          vcgenSessions.Add(impl, vcgen);
        }
      }
      return new ReadOnlyDictionary<Implementation, HoudiniVCGen>(vcgenSessions);
    }

    private ReadOnlyDictionary<string, IdentifierExpr> CollectExistentialConstants(Program program) {
      Dictionary<string, IdentifierExpr> existentialConstants = new Dictionary<string, IdentifierExpr>();
      foreach (Declaration decl in program.TopLevelDeclarations) {
        Constant constant = decl as Constant;
        if (constant != null) {
          bool result = false;
          if (constant.CheckBooleanAttribute("existential", ref result)) {
            if (result == true)
              existentialConstants.Add(constant.Name, new IdentifierExpr(Token.NoToken, constant));
          }
        }
      }
      return new ReadOnlyDictionary<string, IdentifierExpr>(existentialConstants);
    }

    private Graph<Implementation> BuildCallGraph(Program program) {
      Dictionary<Procedure, HashSet<Implementation>> procToImpls = new Dictionary<Procedure, HashSet<Implementation>>();
      foreach (Declaration decl in program.TopLevelDeclarations) {
        Implementation impl = decl as Implementation;
        if (impl == null) continue;
        if (!procToImpls.ContainsKey(impl.Proc))
          procToImpls[impl.Proc] = new HashSet<Implementation>();
        procToImpls[impl.Proc].Add(impl);
      }
      Graph<Implementation> callGraph = new Graph<Implementation>();
      foreach (Declaration decl in program.TopLevelDeclarations) {
        Implementation impl = decl as Implementation;
        if (impl == null) continue;
        foreach (Block b in impl.Blocks) {
          foreach (Cmd c in b.Cmds) {
            CallCmd cc = c as CallCmd;
            if (cc == null) continue;
            foreach (Implementation callee in procToImpls[cc.Proc]) {
              callGraph.AddEdge(impl, callee);
            }
          }
        }
      }
      return callGraph;
    }

    private Queue<Implementation> BuildWorkList(Program program) {
      Queue<Implementation> queue = new Queue<Implementation>();
      foreach (Declaration decl in program.TopLevelDeclarations) {
        Implementation impl = decl as Implementation;
        if (impl != null) {
          queue.Enqueue(impl);
        }
      }
      return queue;
    }

    private bool MatchCandidate(Expr boogieExpr, out string candidateConstant) {
      candidateConstant = null;
      IExpr antecedent;
      IExpr expr = boogieExpr as IExpr;
      if (expr != null && ExprUtil.Match(expr, Prop.Implies, out antecedent)) {
        IdentifierExpr.ConstantFunApp constantFunApp = antecedent as IdentifierExpr.ConstantFunApp;
        if (constantFunApp != null && houdiniConstants.ContainsKey(constantFunApp.IdentifierExpr.Name)) {
          candidateConstant = constantFunApp.IdentifierExpr.Name;
          return true;
        }
      }
      return false;
    }

    private Axiom BuildAxiom(Dictionary<string, bool> currentAssignment) {
      Expr axiom = new LiteralExpr(Token.NoToken, true);
      foreach (KeyValuePair<string, bool> kv in currentAssignment) {
        IdentifierExpr constantExpr;
        houdiniConstants.TryGetValue(kv.Key, out constantExpr);
        Contract.Assume(constantExpr != null);
        Expr valueExpr = new LiteralExpr(Token.NoToken, kv.Value);
        Expr constantAssignment = Expr.Binary(Token.NoToken, BinaryOperator.Opcode.Eq, constantExpr, valueExpr);
        axiom = Expr.Binary(Token.NoToken, BinaryOperator.Opcode.And, axiom, constantAssignment);
      }
      return new Axiom(Token.NoToken, axiom);
    }

    private Dictionary<string, bool> BuildAssignment(Dictionary<string, IdentifierExpr>.KeyCollection constants) {
      Dictionary<string, bool> initial = new Dictionary<string, bool>();
      foreach (string constant in constants)
        initial.Add(constant, true);
      return initial;
    }

    private VCGen.Outcome VerifyUsingAxiom(Implementation implementation, Axiom axiom, out List<Counterexample> errors) {
      HoudiniVCGen vcgen;
      vcgenSessions.TryGetValue(implementation, out vcgen);
      if (vcgen == null)
        throw new Exception("HdnVCGen not found for implementation: " + implementation.Name);
      vcgen.PushAxiom(axiom);
      VCGen.Outcome outcome = TryCatchVerify(vcgen, out errors);
      vcgen.Pop();
      return outcome;
    }

    // the main procedure that checks a procedure and updates the
    // assignment and the worklist
    private VCGen.Outcome HoudiniVerifyCurrent(HoudiniState current,
                                        Program program,
                                        out List<Counterexample> errors,
                                        out bool exc) {
      HoudiniVCGen vcgen;
      if (current.Implementation == null)
        throw new Exception("HoudiniVerifyCurrent has null implementation");

      Implementation implementation = current.Implementation;
      vcgenSessions.TryGetValue(implementation, out vcgen);
      if (vcgen == null)
        throw new Exception("HdnVCGen not found for implementation: " + implementation.Name);

      VCGen.Outcome outcome = HoudiniVerifyCurrentAux(current, program, vcgen, out errors, out exc);
      return outcome;
    }

    private VCGen.Outcome VerifyCurrent(HoudiniState current,
                                        Program program,
                                        out List<Counterexample> errors,
                                        out bool exc) {
      HoudiniVCGen vcgen;
      if (current.Implementation != null) {
        Implementation implementation = current.Implementation;
        vcgenSessions.TryGetValue(implementation, out vcgen);
        if (vcgen == null)
          throw new Exception("HdnVCGen not found for implementation: " + implementation.Name);

        VCGen.Outcome outcome = TrySpinSameFunc(current, program, vcgen, out errors, out exc);
        return outcome;
      }
      else {
        throw new Exception("VerifyCurrent has null implementation");
      }
    }

    private bool IsOutcomeNotHoudini(VCGen.Outcome outcome, List<Counterexample> errors) {
      switch (outcome) {
        case VCGen.Outcome.Correct:
          return false;
        case VCGen.Outcome.Errors:
          Contract.Assume(errors != null);
          foreach (Counterexample error in errors) {
            if (ExtractRefutedAnnotation(error) == null)
              return true;
          }
          return false;
        case VCGen.Outcome.TimedOut:
        case VCGen.Outcome.Inconclusive:
        default:
          return true;
      }
    }

    // returns true if at least one of the violations is non-candidate
    private bool AnyNonCandidateViolation(VCGen.Outcome outcome, List<Counterexample> errors) {
      switch (outcome) {
        case VCGen.Outcome.Errors:
          Contract.Assert(errors != null);
          foreach (Counterexample error in errors) {
            if (ExtractRefutedAnnotation(error) == null)
              return true;
          }
          return false;
        case VCGen.Outcome.Correct:
        case VCGen.Outcome.TimedOut:
        case VCGen.Outcome.Inconclusive:
        default:
          return false;
      }
    }

    private List<Counterexample> emptyList = new List<Counterexample>();

    // Record most current Non-Candidate errors found by Boogie, etc.
    private void UpdateHoudiniOutcome(HoudiniOutcome houdiniOutcome,
                                      Implementation implementation,
                                      VCGen.Outcome verificationOutcome,
                                      List<Counterexample> errors) {
      string implName = implementation.ToString();
      houdiniOutcome.implementationOutcomes.Remove(implName);
      List<Counterexample> nonCandidateErrors = new List<Counterexample>();

      switch (verificationOutcome) {
        case VCGen.Outcome.Errors:
          Contract.Assume(errors != null);
          foreach (Counterexample error in errors) {
            if (ExtractRefutedAnnotation(error) == null)
              nonCandidateErrors.Add(error);
          }
          break;
        case VCGen.Outcome.TimedOut:
        case VCGen.Outcome.Correct:
        case VCGen.Outcome.Inconclusive:
        default:
          break;
      }
      houdiniOutcome.implementationOutcomes.Add(implName, new VCGenOutcome(verificationOutcome, nonCandidateErrors));
    }

    private void FlushWorkList(HoudiniState current) {
      this.NotifyFlushStart();
      Axiom axiom = BuildAxiom(current.Assignment);
      while (current.WorkList.Count > 0) {
        this.NotifyIteration();

        current.Implementation = current.WorkList.Peek();
        this.NotifyImplementation(current.Implementation);

        List<Counterexample> errors;
        VCGen.Outcome outcome = VerifyUsingAxiom(current.Implementation, axiom, out errors);
        UpdateHoudiniOutcome(current.Outcome, current.Implementation, outcome, errors);
        this.NotifyOutcome(outcome);

        current.WorkList.Dequeue();
        this.NotifyDequeue();

      }
      this.NotifyFlushFinish();
    }

    private void UpdateAssignment(HoudiniState current, RefutedAnnotation refAnnot) {
      current.Assignment.Remove(refAnnot.Constant);
      current.Assignment.Add(refAnnot.Constant, false);
      this.NotifyConstant(refAnnot.Constant);
    }

    private void AddToWorkList(HoudiniState current, Implementation imp) {
      if (!current.WorkList.Contains(imp) && !current.isBlackListed(imp.Name)) {
        current.WorkList.Enqueue(imp);
        this.NotifyEnqueue(imp);
      }
    }

    private void UpdateWorkList(HoudiniState current,
                                VCGen.Outcome outcome,
                                List<Counterexample> errors) {
      Contract.Assume(current.Implementation != null);

      switch (outcome) {
        case VCGen.Outcome.Correct:
          current.WorkList.Dequeue();
          this.NotifyDequeue();
          break;
        case VCGen.Outcome.Errors:
          Contract.Assume(errors != null);
          bool dequeue = false;
          foreach (Counterexample error in errors) {
            RefutedAnnotation refutedAnnotation = ExtractRefutedAnnotation(error);
            if (refutedAnnotation != null) {
              foreach (Implementation implementation in FindImplementationsToEnqueue(refutedAnnotation, current.Implementation)) { AddToWorkList(current, implementation); }
              UpdateAssignment(current, refutedAnnotation);
            }
            else {
              dequeue = true; //once one non-houdini error is hit dequeue?!
            }
          }
          if (dequeue) {
            current.WorkList.Dequeue();
            this.NotifyDequeue();
          }
          break;
        case VCGen.Outcome.TimedOut:
          // TODO: reset session instead of blocking timed out funcs?
          current.addToBlackList(current.Implementation.Name);
          current.WorkList.Dequeue();
          this.NotifyDequeue();
          break;
        case VCGen.Outcome.OutOfMemory:
        case VCGen.Outcome.Inconclusive:
          current.WorkList.Dequeue();
          this.NotifyDequeue();
          break;
        default:
          throw new Exception("Unknown vcgen outcome");
      }
    }


    private void AddRelatedToWorkList(HoudiniState current, RefutedAnnotation refutedAnnotation) {
      Contract.Assume(current.Implementation != null);
      foreach (Implementation implementation in FindImplementationsToEnqueue(refutedAnnotation, current.Implementation)) {
        AddToWorkList(current, implementation);
      }
    }


    // Updates the worklist and current assignment
    // @return true if the current function is kept on the queue
    private bool UpdateAssignmentWorkList(HoudiniState current,
                                          VCGen.Outcome outcome,
                                          List<Counterexample> errors) {
      Contract.Assume(current.Implementation != null);
      bool dequeue = true;

      switch (outcome) {
        case VCGen.Outcome.Correct:
          //yeah, dequeue
          break;
        case VCGen.Outcome.Errors:
          Contract.Assume(errors != null);
          foreach (Counterexample error in errors) {
            RefutedAnnotation refutedAnnotation = ExtractRefutedAnnotation(error);
            if (refutedAnnotation != null) { // some candidate annotation removed
              AddRelatedToWorkList(current, refutedAnnotation);
              UpdateAssignment(current, refutedAnnotation);
              dequeue = false;
            }
          }
          break;

        case VCGen.Outcome.TimedOut:
          // TODO: reset session instead of blocking timed out funcs?
          current.addToBlackList(current.Implementation.Name);
          break;
        case VCGen.Outcome.Inconclusive:
        case VCGen.Outcome.OutOfMemory:
          break;
        default:
          throw new Exception("Unknown vcgen outcome");
      }
      if (dequeue) {
        current.WorkList.Dequeue();
        this.NotifyDequeue();
      }
      return !dequeue;
    }

    private class HoudiniState {
      private Queue<Implementation> _workList;
      private HashSet<string> blackList;
      private Dictionary<string, bool> _assignment;
      private Implementation _implementation;
      private HoudiniOutcome _outcome;

      public HoudiniState(Queue<Implementation> workList, Dictionary<string, bool> currentAssignment) {
        this._workList = workList;
        this._assignment = currentAssignment;
        this._implementation = null;
        this._outcome = new HoudiniOutcome();
        this.blackList = new HashSet<string>();
      }

      public Queue<Implementation> WorkList {
        get { return this._workList; }
      }
      public Dictionary<string, bool> Assignment {
        get { return this._assignment; }
      }
      public Implementation Implementation {
        get { return this._implementation; }
        set { this._implementation = value; }
      }
      public HoudiniOutcome Outcome {
        get { return this._outcome; }
      }
      public bool isBlackListed(string funcName) {
        return blackList.Contains(funcName);
      }
      public void addToBlackList(string funcName) {
        blackList.Add(funcName);
      }
    }

    private void PrintBadList(string kind, List<string> list) {
      if (list.Count != 0) {
        Console.WriteLine("----------------------------------------");
        Console.WriteLine("Functions: {0}", kind);
        foreach (string fname in list) {
          Console.WriteLine("\t{0}", fname);
        }
        Console.WriteLine("----------------------------------------");
      }
    }

    private void PrintBadOutcomes(List<string> timeouts, List<string> inconc, List<string> errors) {
      PrintBadList("TimedOut", timeouts);
      PrintBadList("Inconclusive", inconc);
      PrintBadList("Errors", errors);
    }

    public HoudiniOutcome VerifyProgram(Program program) {
      HoudiniOutcome outcome = VerifyProgramSameFuncFirst(program);
      PrintBadOutcomes(outcome.ListOfTimeouts, outcome.ListOfInconclusives, outcome.ListOfErrors);
      return outcome;
    }

    // Old main loop
    public HoudiniOutcome VerifyProgramUnorderedWork(Program program) {
      HoudiniState current = new HoudiniState(BuildWorkList(program), BuildAssignment(houdiniConstants.Keys));
      this.NotifyStart(program, houdiniConstants.Keys.Count);

      while (current.WorkList.Count > 0) {
        System.GC.Collect();
        this.NotifyIteration();

        Axiom axiom = BuildAxiom(current.Assignment);
        this.NotifyAssignment(current.Assignment);

        current.Implementation = current.WorkList.Peek();
        this.NotifyImplementation(current.Implementation);

        List<Counterexample> errors;
        VCGen.Outcome outcome = VerifyUsingAxiom(current.Implementation, axiom, out errors);
        this.NotifyOutcome(outcome);

        UpdateHoudiniOutcome(current.Outcome, current.Implementation, outcome, errors);
        if (IsOutcomeNotHoudini(outcome, errors) && !continueAtError) {
          current.WorkList.Dequeue();
          this.NotifyDequeue();
          FlushWorkList(current);
        }
        else
          UpdateWorkList(current, outcome, errors);
      }
      this.NotifyEnd(true);
      current.Outcome.assignment = current.Assignment;
      return current.Outcome;
    }

    // New main loop
    public HoudiniOutcome VerifyProgramSameFuncFirst(Program program) {
      HoudiniState current = new HoudiniState(BuildWorkList(program), BuildAssignment(houdiniConstants.Keys));
      this.NotifyStart(program, houdiniConstants.Keys.Count);

      while (current.WorkList.Count > 0) {
        bool exceptional = false;
        System.GC.Collect();
        this.NotifyIteration();

        current.Implementation = current.WorkList.Peek();
        this.NotifyImplementation(current.Implementation);

        List<Counterexample> errors;
        VCGen.Outcome outcome = VerifyCurrent(current, program, out errors, out exceptional);

        // updates to worklist already done in VerifyCurrent, unless there was an exception
        if (exceptional) {
          this.NotifyOutcome(outcome);
          UpdateHoudiniOutcome(current.Outcome, current.Implementation, outcome, errors);
          if (IsOutcomeNotHoudini(outcome, errors) && !continueAtError) {
            current.WorkList.Dequeue();
            this.NotifyDequeue();
            FlushWorkList(current);
          }
          else {
            UpdateAssignmentWorkList(current, outcome, errors);
          }
          exceptional = false;
        }
      }
      this.NotifyEnd(true);
      current.Outcome.assignment = current.Assignment;
      return current.Outcome;
    }

    //Clean houdini (Based on "Houdini Spec in Boogie" email 10/22/08
    //Aborts when there is a violation of non-candidate assertion
    //This can be used in eager mode (continueAfterError) by simply making
    //all non-candidate annotations as unchecked (free requires/ensures, assumes)
    public HoudiniOutcome PerformHoudiniInference(Program program) {
      HoudiniState current = new HoudiniState(BuildWorkList(program), BuildAssignment(houdiniConstants.Keys));
      this.NotifyStart(program, houdiniConstants.Keys.Count);

      Console.WriteLine("Using the new houdini algorithm\n");

      while (current.WorkList.Count > 0) {
        bool exceptional = false;
        System.GC.Collect();
        this.NotifyIteration();

        current.Implementation = current.WorkList.Peek();
        this.NotifyImplementation(current.Implementation);

        List<Counterexample> errors;
        VCGen.Outcome outcome = HoudiniVerifyCurrent(current, program, out errors, out exceptional);

        // updates to worklist already done in VerifyCurrent, unless there was an exception
        if (exceptional) {
          this.NotifyOutcome(outcome);
          UpdateHoudiniOutcome(current.Outcome, current.Implementation, outcome, errors);
          if (AnyNonCandidateViolation(outcome, errors)) { //abort
            current.WorkList.Dequeue();
            this.NotifyDequeue();
            FlushWorkList(current);
          }
          else { //continue
            UpdateAssignmentWorkList(current, outcome, errors);
          }
        }
      }
      this.NotifyEnd(true);
      current.Outcome.assignment = current.Assignment;
      return current.Outcome;
    }

    private List<Implementation> FindImplementationsToEnqueue(RefutedAnnotation refutedAnnotation, Implementation currentImplementation) {
      List<Implementation> implementations = new List<Implementation>();
      switch (refutedAnnotation.Kind) {
        case RefutedAnnotationKind.REQUIRES:
          foreach (Implementation callee in callGraph.Successors(currentImplementation)) {
            Contract.Assume(callee.Proc != null);
            if (callee.Proc.Equals(refutedAnnotation.CalleeProc))
              implementations.Add(callee);
          }
          break;
        case RefutedAnnotationKind.ENSURES:
          foreach (Implementation caller in callGraph.Predecessors(currentImplementation))
            implementations.Add(caller);
          break;
        case RefutedAnnotationKind.ASSERT: //the implementation is already in queue
          break;
        default:
          throw new Exception("Unknown Refuted annotation kind:" + refutedAnnotation.Kind);
      }
      return implementations;
    }

    private enum RefutedAnnotationKind { REQUIRES, ENSURES, ASSERT };

    private class RefutedAnnotation {
      private string _constant;
      private RefutedAnnotationKind _kind;
      private Procedure _callee;

      private RefutedAnnotation(string constant, RefutedAnnotationKind kind, Procedure callee) {
        this._constant = constant;
        this._kind = kind;
        this._callee = callee;
      }
      public RefutedAnnotationKind Kind {
        get { return this._kind; }
      }
      public string Constant {
        get { return this._constant; }
      }
      public Procedure CalleeProc {
        get { return this._callee; }
      }
      public static RefutedAnnotation BuildRefutedRequires(string constant, Procedure callee) {
        return new RefutedAnnotation(constant, RefutedAnnotationKind.REQUIRES, callee);
      }
      public static RefutedAnnotation BuildRefutedEnsures(string constant) {
        return new RefutedAnnotation(constant, RefutedAnnotationKind.ENSURES, null);
      }
      public static RefutedAnnotation BuildRefutedAssert(string constant) {
        return new RefutedAnnotation(constant, RefutedAnnotationKind.ASSERT, null);
      }

    }

    private void PrintRefutedCall(CallCounterexample err, XmlSink xmlOut) {
      Expr cond = err.FailingRequires.Condition;
      string houdiniConst;
      if (MatchCandidate(cond, out houdiniConst)) {
        xmlOut.WriteError("precondition violation", err.FailingCall.tok, err.FailingRequires.tok, err.Trace);
      }
    }

    private void PrintRefutedReturn(ReturnCounterexample err, XmlSink xmlOut) {
      Expr cond = err.FailingEnsures.Condition;
      string houdiniConst;
      if (MatchCandidate(cond, out houdiniConst)) {
        xmlOut.WriteError("postcondition violation", err.FailingReturn.tok, err.FailingEnsures.tok, err.Trace);
      }
    }

    private void PrintRefutedAssert(AssertCounterexample err, XmlSink xmlOut) {
      Expr cond = err.FailingAssert.OrigExpr;
      string houdiniConst;
      if (MatchCandidate(cond, out houdiniConst)) {
        xmlOut.WriteError("postcondition violation", err.FailingAssert.tok, err.FailingAssert.tok, err.Trace);
      }
    }


    private void DebugRefutedCandidates(Implementation curFunc, List<Counterexample> errors) {
      XmlSink xmlRefuted = CommandLineOptions.Clo.XmlRefuted;
      if (xmlRefuted != null && errors != null) {
        DateTime start = DateTime.Now;
        xmlRefuted.WriteStartMethod(curFunc.ToString(), start);

        foreach (Counterexample error in errors) {
          CallCounterexample ce = error as CallCounterexample;
          if (ce != null) PrintRefutedCall(ce, xmlRefuted);
          ReturnCounterexample re = error as ReturnCounterexample;
          if (re != null) PrintRefutedReturn(re, xmlRefuted);
          AssertCounterexample ae = error as AssertCounterexample;
          if (ae != null) PrintRefutedAssert(ae, xmlRefuted);
        }

        DateTime end = DateTime.Now;
        xmlRefuted.WriteEndMethod("errors", end, end.Subtract(start));
      }
    }

    private RefutedAnnotation ExtractRefutedAnnotation(Counterexample error) {
      string houdiniConstant;
      CallCounterexample callCounterexample = error as CallCounterexample;
      if (callCounterexample != null) {
        Procedure failingProcedure = callCounterexample.FailingCall.Proc;
        Requires failingRequires = callCounterexample.FailingRequires;
        if (MatchCandidate(failingRequires.Condition, out houdiniConstant)) {
          Contract.Assert(houdiniConstant != null);
          return RefutedAnnotation.BuildRefutedRequires(houdiniConstant, failingProcedure);
        }
      }
      ReturnCounterexample returnCounterexample = error as ReturnCounterexample;
      if (returnCounterexample != null) {
        Ensures failingEnsures = returnCounterexample.FailingEnsures;
        if (MatchCandidate(failingEnsures.Condition, out houdiniConstant)) {
          Contract.Assert(houdiniConstant != null);
          return RefutedAnnotation.BuildRefutedEnsures(houdiniConstant);
        }
      }
      AssertCounterexample assertCounterexample = error as AssertCounterexample;
      if (assertCounterexample != null) {
        AssertCmd failingAssert = assertCounterexample.FailingAssert;
        if (MatchCandidate(failingAssert.OrigExpr, out houdiniConstant)) {
          Contract.Assert(houdiniConstant != null);
          return RefutedAnnotation.BuildRefutedAssert(houdiniConstant);
        }
      }

      return null;
    }

    private VCGen.Outcome TryCatchVerify(HoudiniVCGen vcgen, out List<Counterexample> errors) {
      VCGen.Outcome outcome;
      try {
        outcome = vcgen.Verify(out errors);
      }
      catch (VCGenException e) {
        Contract.Assume(e != null);
        errors = null;
        outcome = VCGen.Outcome.Inconclusive;
      }
      catch (UnexpectedProverOutputException upo) {
        Contract.Assume(upo != null);
        errors = null;
        outcome = VCGen.Outcome.Inconclusive;
      }
      return outcome;
    }

    //version of TryCatchVerify that spins on the same function
    //as long as the current assignment is changing
    private VCGen.Outcome TrySpinSameFunc(HoudiniState current,
                                          Program program,
                                          HoudiniVCGen vcgen,
                                          out List<Counterexample> errors,
                                          out bool exceptional) {
      Contract.Assert(current.Implementation != null);
      VCGen.Outcome outcome;
      bool pushed = false;
      errors = null;
      outcome = VCGen.Outcome.Inconclusive;
      try {
        bool trySameFunc = true;
        bool pastFirstIter = false; //see if this new loop is even helping

        do {
          if (pastFirstIter) {
            System.GC.Collect();
            this.NotifyIteration();
          }
          Axiom currentAx = BuildAxiom(current.Assignment);
          this.NotifyAssignment(current.Assignment);

          vcgen.PushAxiom(currentAx);
          pushed = true;
          outcome = vcgen.Verify(out errors);
          vcgen.Pop();
          pushed = false;
          this.NotifyOutcome(outcome);

          DebugRefutedCandidates(current.Implementation, errors);
          UpdateHoudiniOutcome(current.Outcome, current.Implementation, outcome, errors);
          if (!continueAtError && IsOutcomeNotHoudini(outcome, errors)) {
            current.WorkList.Dequeue();
            this.NotifyDequeue();
            trySameFunc = false;
            FlushWorkList(current);
          }
          else {
            trySameFunc = UpdateAssignmentWorkList(current, outcome, errors);
            //reset for the next round
            errors = null;
            outcome = VCGen.Outcome.Inconclusive;
          }
          pastFirstIter = true;
        } while (trySameFunc && current.WorkList.Count > 0);

      }
      catch (VCGenException e) {
        Contract.Assume(e != null);
        if (pushed) {
          vcgen.Pop(); // what if session is dead?
        }
        NotifyException("VCGen");
        exceptional = true;
        return outcome;
      }
      catch (UnexpectedProverOutputException upo) {
        Contract.Assume(upo != null);
        if (pushed) {
          vcgen.Pop();
        }
        NotifyException("UnexpectedProverOutput");
        exceptional = true;
        return outcome;
      }
      exceptional = false;
      return outcome;
    }

    //Similar to TrySpinSameFunc except no Candidate logic
    private VCGen.Outcome HoudiniVerifyCurrentAux(HoudiniState current,
                                          Program program,
                                          HoudiniVCGen vcgen,
                                          out List<Counterexample> errors,
                                          out bool exceptional) {
      Contract.Assert(current.Implementation != null);
      VCGen.Outcome outcome;
      bool pushed = false;
      errors = null;
      outcome = VCGen.Outcome.Inconclusive;
      try {
        bool trySameFunc = true;
        bool pastFirstIter = false; //see if this new loop is even helping

        do {
          if (pastFirstIter) {
            System.GC.Collect();
            this.NotifyIteration();
          }

          Axiom currentAx = BuildAxiom(current.Assignment);
          this.NotifyAssignment(current.Assignment);

          //check the VC with the current assignment
          vcgen.PushAxiom(currentAx);
          pushed = true;
          outcome = vcgen.Verify(out errors);
          vcgen.Pop();
          pushed = false;
          this.NotifyOutcome(outcome);

          DebugRefutedCandidates(current.Implementation, errors);
          UpdateHoudiniOutcome(current.Outcome, current.Implementation, outcome, errors);

          if (AnyNonCandidateViolation(outcome, errors)) { //abort
            current.WorkList.Dequeue();
            this.NotifyDequeue();
            trySameFunc = false;
            FlushWorkList(current);
          }
          else { //continue
            trySameFunc = UpdateAssignmentWorkList(current, outcome, errors);
            //reset for the next round
            errors = null;
            outcome = VCGen.Outcome.Inconclusive;
          }
          pastFirstIter = true;
        } while (trySameFunc && current.WorkList.Count > 0);

      }
      catch (VCGenException e) {
        Contract.Assume(e != null);
        if (pushed) {
          vcgen.Pop(); // what if session is dead?
        }
        NotifyException("VCGen");
        exceptional = true;
        return outcome;
      }
      catch (UnexpectedProverOutputException upo) {
        Contract.Assume(upo != null);
        if (pushed) {
          vcgen.Pop();
        }
        NotifyException("UnexpectedProverOutput");
        exceptional = true;
        return outcome;
      }
      exceptional = false;
      return outcome;
    }
  }

  public enum HoudiniOutcomeKind { Done, FatalError, VerificationCompleted }

  public class VCGenOutcome {
    public VCGen.Outcome outcome;
    public List<Counterexample> errors;
    public VCGenOutcome(VCGen.Outcome outcome, List<Counterexample> errors) {
      this.outcome = outcome;
      this.errors = errors;
    }
  }

  public class HoudiniOutcome {
    // final assignment
    public Dictionary<string, bool> assignment = new Dictionary<string, bool>();
    // boogie errors
    public Dictionary<string, VCGenOutcome> implementationOutcomes = new Dictionary<string, VCGenOutcome>();
    // outcome kind    
    public HoudiniOutcomeKind kind;

    // statistics 

    private int CountResults(VCGen.Outcome outcome) {
      int outcomeCount = 0;
      foreach (VCGenOutcome verifyOutcome in implementationOutcomes.Values) {
        if (verifyOutcome.outcome == outcome)
          outcomeCount++;
      }
      return outcomeCount;
    }

    private List<string> ListOutcomeMatches(VCGen.Outcome outcome) {
      List<string> result = new List<string>();
      foreach (KeyValuePair<string, VCGenOutcome> kvpair in implementationOutcomes) {
        if (kvpair.Value.outcome == outcome)
          result.Add(kvpair.Key);
      }
      return result;
    }

    public int ErrorCount {
      get {
        return CountResults(VCGen.Outcome.Errors);
      }
    }
    public int Verified {
      get {
        return CountResults(VCGen.Outcome.Correct);
      }
    }
    public int Inconclusives {
      get {
        return CountResults(VCGen.Outcome.Inconclusive);
      }
    }
    public int TimeOuts {
      get {
        return CountResults(VCGen.Outcome.TimedOut);
      }
    }
    public List<string> ListOfTimeouts {
      get {
        return ListOutcomeMatches(VCGen.Outcome.TimedOut);
      }
    }
    public List<string> ListOfInconclusives {
      get {
        return ListOutcomeMatches(VCGen.Outcome.Inconclusive);
      }
    }
    public List<string> ListOfErrors {
      get {
        return ListOutcomeMatches(VCGen.Outcome.Errors);
      }
    }
  }

}