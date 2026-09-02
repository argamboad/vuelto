# Preface

## What this book is

This is a course disguised as a book. Over ten parts and fifty lessons, you will build —
by typing every hand-written line yourself — a production-grade, multi-tenant SaaS
platform: custom JWT authentication with passwordless sign-in, OAuth, and MFA; tenant
isolation enforced by the type system *and* by the database; billing with Stripe;
reliable asynchronous work via a transactional outbox; role-based access control; file
storage; GDPR export and erasure; a public API; outbound webhooks; an admin back-office;
full observability — and at the end you will deploy it to a real host behind a real CI/CD
pipeline that you also built.

The code is real. Every excerpt in this book is drawn from a working reference platform
(Perezosoft Platform), not invented for the page. A coverage gate — a script, in the same
spirit as the tests you'll write — maps every file of that platform to the lesson that
builds it, so nothing is skipped and nothing appears out of thin air.

But the code is not the point. The point is the two habits that separate an architect
from a very fast typist:

1. **Making invariants structural.** Not "remember to filter by tenant" but "a query
   *cannot* forget to filter by tenant." Not "please don't call this API in feature code"
   but "the build fails if you do." You will meet this move dozens of times — in query
   filters, interceptors, marker interfaces, architecture tests, CI gates, database
   policies — until reaching for it is a reflex.
2. **Recording decisions.** Every lesson ends with an *Architecture Decision* box: the
   fork that was faced, the option chosen, the options rejected, and the price paid. You
   will write your own decision log from lesson 0.1 onward, before any code exists.
   Six months from now, "why is it like this?" will have an answer.

## Who this is for

You are comfortable in C# — classes, interfaces, async/await, LINQ hold no fear — and
you can find your way around a terminal and git. You have probably built applications,
perhaps professionally, but you want the *architecture* to stop feeling like folklore:
why layers, why seams, why tests first, why all these patterns with strange names, and —
above all — when *not* to use them.

You do not need prior experience with multi-tenancy, EF Core internals, Stripe, Docker,
OAuth, or GitHub Actions. Each is taught when the platform needs it, motivated by a
concrete problem you will feel before you solve it.

## What this book is not

Honesty up front: this is a course in **systems design and engineering practice**, not
computational problem-solving. You will find no algorithm puzzles and almost no
performance engineering here — no profilers, and caching is deliberately deferred (a
recorded decision you'll read about). If you want to get better at data structures, this
is the wrong book; if you want to get better at *building software that survives contact
with production and with other people*, it is the right one.

## The rhythm

Every lesson follows the same beat, and the beat is the curriculum:

1. **Motivate** — a concrete problem, ideally demonstrated by a failing or *leaking* test.
2. **Red** — write the test that pins the behavior you want. It fails.
3. **Green** — write the minimum code to pass it.
4. **Refactor & harden** — extract the seam; add the guardrail that makes the mistake
   impossible to repeat.
5. **Run it** — commands and expected output; every lesson ends with the app working.
6. **Architecture Decision** — the fork, the choice, the road not taken.
7. **Checkpoint** — a git tag; what you should now be able to do.

Test-driven development is not a chapter in this book; it is the loop you will live
roughly fifty times, exactly as the reference platform was actually built.

## How to use this book

**The companion repository.** Alongside the book there is a companion repo built in
teaching order, with one tagged checkpoint per lesson (`lesson-1.3`, `lesson-2.6`, …).
Type everything yourself — that is the course — but when you are stuck, `git diff` your
work against the checkpoint, or check it out and keep moving. Falling behind the typing
is recoverable; skipping the understanding is not.

**Don't skip Part 0.** Lesson 0.2 pins your toolchain and infrastructure so that the
other forty-nine lessons behave identically on your machine. Most abandoned courses die
at "works on my machine"; we kill that failure mode first.

**When a lesson cites a rule** (R1–R35) or an ADR, that is a pointer into the reference
platform's own governance documents — the machine-enforced invariants and the decision
log. You are not just building an app; you are building the *system that keeps the app
honest*, and those citations show you where each piece of it came from.

**Reading order is dependency order.** Later lessons lean on earlier seams without
re-explaining them. If Part 5 feels steep, the fix is usually two parts back, not a
re-read of Part 5.

---

# The platform at a glance

<!-- figure: arch-solution-map | The whole platform on one page — clients, the server onion, and the API boundary between them (ARCHITECTURE.md §1) -->

The clients speak HTTP/JSON to `src/Api` and share no compiled code with it;
inside the server, dependencies point strictly inward — `Api` and
`Infrastructure` both depend on `Core`, and `Core` depends on nothing. The
seams (small interfaces like `IEmailSender`) live in `Core`; their
implementations live in `Infrastructure`; PostgreSQL sits underneath it all.

One sentence to keep for the whole book: **app data belongs to the tenant, not the
user** — and by the end of Part 2 you will have made it impossible, at three separate
layers, for any line of feature code to violate that sentence.
