// RUN: %parallel-boogie "%s" > "%t"
// RUN: %diff "%s.expect" "%t"

type {:linear "tid"} Tid = int;

var {:layer 0,5} x: int;
const unique p: int;
const unique q: int;
const unique r: int;

yield procedure {:layer 0} P0({:linear "tid"} tid: int);
refines AtomicP0;
left action {:layer 1} AtomicP0({:linear "tid"} tid: int)
modifies x;
{ assert tid == p; assert x == 0; x := 1; }

yield procedure {:layer 0} Q0({:linear "tid"} tid: int);
refines AtomicQ0;
atomic action {:layer 1} AtomicQ0({:linear "tid"} tid: int)
modifies x;
{ assert tid == q; assume x == 1; x := 2; }

yield procedure {:layer 0} R0({:linear "tid"} tid: int);
refines AtomicR0;
atomic action {:layer 1,3} AtomicR0({:linear "tid"} tid: int)
modifies x;
{ assert tid == r; assume x == 2; x := 3; }

yield procedure {:layer 1} P1({:linear_in "tid"} tid: int)
refines AtomicP1;
{  async call {:sync} P0(tid);  }
atomic action {:layer 2,3} AtomicP1({:linear_in "tid"} tid: int)
modifies x;
{ assert tid == p; assert x == 0; x := 1; }

yield procedure {:layer 1} Q1({:linear "tid"} tid: int)
refines AtomicQ1;
{  call Q0(tid);  }
left action {:layer 2,3} AtomicQ1({:linear "tid"} tid: int)
modifies x;
{ assert tid == q; assert x == 1; x := 2; }

yield procedure {:layer 3} R3({:linear "tid"} tid: int)
refines AtomicR3;
{  call R0(tid);  }
left action {:layer 4} AtomicR3({:linear "tid"} tid: int)
modifies x;
{ assert tid == r; assert x == 2; x := 3; }

yield procedure {:layer 3} Main3({:linear_in "tid"} tidp: int, {:linear_in "tid"} tidq: int)
refines AtomicMain3;
{
    call P1(tidp);
    async call {:sync} Q1(tidq);
}
atomic action {:layer 4} AtomicMain3({:linear_in "tid"} tidp: int, {:linear_in "tid"} tidq: int)
modifies x;
{ assert tidp == p && tidq == q; assert x == 0; x := 2; }

yield procedure {:layer 4} Main4({:linear_in "tid"} tidp: int, {:linear_in "tid"} tidq: int, {:linear_in "tid"} tidr: int)
refines AtomicMain4;
{
    call Main3(tidp, tidq);
    async call {:sync} R3(tidr);
}
atomic action {:layer 5} AtomicMain4({:linear_in "tid"} tidp: int, {:linear_in "tid"} tidq: int, {:linear_in "tid"} tidr: int)
modifies x;
{ assert tidp == p && tidq == q && tidr == r; assert x == 0; x := 3; }
