$base = "http://localhost:54169"
Remove-Item cookies.txt -ErrorAction SilentlyContinue
Remove-Item login.html, reminders.html -ErrorAction SilentlyContinue

# 1. GET login page
curl.exe -s -c cookies.txt -b cookies.txt -o login.html "$base/Account/Login"
$loginHtml = Get-Content login.html -Raw
$m = [regex]::Match($loginHtml, "name=""__RequestVerificationToken""\s+type=""hidden""\s+value=""([^""]+)""")
if (-not $m.Success) {
    $m = [regex]::Match($loginHtml, "__RequestVerificationToken""[^>]*value=""([^""]+)""")
}
"Token found: $($m.Success), len=$($m.Groups[1].Value.Length)"
$token = $m.Groups[1].Value

# 2. POST login (let curl follow redirects; cookies.txt persists auth)
Add-Type -AssemblyName System.Web
$encTok = [System.Web.HttpUtility]::UrlEncode($token)
curl.exe -s -c cookies.txt -b cookies.txt -L -o NUL -w "POST login -> http=%{http_code} final_url=%{url_effective}`n" -X POST "$base/Account/Login" --data "Username=admin&Password=Admin123!&__RequestVerificationToken=$encTok"

# 3. Look at the cookie jar
"Cookies set:"
Get-Content cookies.txt | Where-Object { $_ -notmatch "^#" -and $_.Trim() } | ForEach-Object { ($_ -split "`t")[5..6] -join " = " }

# 4. GET /violations with the session cookie
curl.exe -s -c cookies.txt -b cookies.txt -o reminders.html -w "GET /violations -> %{http_code}`n" "$base/violations"
$html = Get-Content reminders.html -Raw
"reminders.html length: $($html.Length)"

# 5. Marker check
$markers = @(
    "class=""data-table sortable""",
    "group-header",
    "data-sort=",
    "data-sort-type=""date""",
    "data-sort-type=""num""",
    "data-no-sort",
    "<h1>Reminders</h1>",
    "Pending reminders",
    "Sign in"
)
foreach ($m in $markers) { "{0,-45} : {1}" -f $m, ($html.Contains($m)) }
