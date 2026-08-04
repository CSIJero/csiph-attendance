# Heartbeat load and security test

Run the application, then execute:

```powershell
pwsh .\Tests\heartbeat-load-security.ps1 `
  -BaseUrl http://localhost:5000 `
  -Users 100 `
  -DurationSeconds 60
```

By default, the test creates 100 independent authenticated sessions using the
demo `admin` account. This exercises concurrent HTTP sessions and SQLite write
contention, but it does not prove that 100 distinct user records behave
correctly.

Client startup is spread across 15 seconds by default to model users whose
browser heartbeats are not perfectly synchronized. Set `-StartupSpreadSeconds 0`
for a worst-case simultaneous burst.

For 100 distinct users, provide a CSV with `Username` and `Password` columns:

```powershell
pwsh .\Tests\heartbeat-load-security.ps1 `
  -BaseUrl https://attendance.example.com `
  -Users 100 `
  -DurationSeconds 300 `
  -CredentialCsv .\heartbeat-test-users.csv
```

Use only test accounts and do not commit the credential CSV.

The test fails when:

- any client cannot log in;
- heartbeat errors exceed 1%;
- heartbeat p95 latency exceeds 1,000 ms; or
- a security probe needs review.

Use `-MaximumErrorPercent` and `-MaximumP95Milliseconds` to change the load
thresholds. Use `-SkipCertificateCheck` only against a local test environment
with a development certificate.
