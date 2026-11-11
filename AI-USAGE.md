# AI Usage Declaration

## Tools

| Tool | Purpose |
|---|---|
| **Claude Code** (Anthropic, model Claude Opus 5.5) | AI coding assistant: generating code, tests and documentation from my requirements and decisions, running builds and tests, and code explanations |
| **Visual Studio 2022** | Running, debugging and checking the solution, and inspecting the database (SQL Server Object Explorer) |

## How AI was used

I used Claude Code as a coding assistant throughout the project. I set the scope and the delivery plan, made
the main decisions, and worked in stages: Claude Code produced the implementation and the tests, and I ran and
checked the results in Visual Studio.

- **Requirements.** Clarifying the brief, including the request contract assumption stated in DESIGN-NOTES.md.
- **Implementation.** The code, the EF Core migration, the admin API, the automated tests and the documentation
  were generated with Claude Code.
- **Decisions.** The scope and plan of the work, and keeping caller authentication out of scope while
  documenting how it would be added (DESIGN-NOTES.md, section 11).
- **Verification.** I ran the solution in Visual Studio, exercised it through Swagger and the `.http` requests,
  checked the audit records in the database, and stepped through the request pipeline in the debugger.
- **Defects.** The defects in DEFECT-REPORT.md were found during these build-and-run cycles.

Commits made with Claude Code carry a `Co-Authored-By: Claude` trailer.

## Responsibility

I take responsibility for the submitted solution. I reviewed the design decisions recorded in DESIGN-NOTES.md and
can explain, trace and modify the code.
