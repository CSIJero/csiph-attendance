# Cpolar Setup Guide for CSI Attendance Monitoring

## Quick Setup Steps

### Step 1: Download Cpolar
1. Go to: https://dashboard.cpolar.com/download
2. Download "cpolar-v3-windows-amd64.zip"
3. Install/extract so `cpolar.exe` is available at `C:\Program Files\cpolar`

### Step 2: Authenticate Cpolar
Open PowerShell and run:
```powershell
cd "C:\Program Files\cpolar"
.\cpolar.exe authtoken NjhmMjQyYTMtOTMyMy00Y2NjLTgzYzktMmQ4NDk4ZTgyYmVl
```

### Step 3: Build Your App
Open another PowerShell window and run:
```powershell
cd C:\Code\csiph-attendance-main
dotnet build
```

### Step 4: Start Your App
In the same window, run:
```powershell
dotnet run
```

Your app will start on: `http://localhost:54169`

Keep this window open!

### Step 5: Create Cpolar Tunnel
Open a THIRD PowerShell window and run:
```powershell
cd "C:\Program Files\cpolar"
.\cpolar.exe http 54169 -region cn -subdomain csi-attendance -daemon on
```

Your app will now be available at:
**https://csi-attendance.cpolar.cn**

## Default Credentials
- Username: `admin`
- Password: `Admin123!`

**⚠️ Change these immediately after first login!**

## Troubleshooting

### "Port 54169 already in use"
- Make sure you're not running the app twice
- Close other windows and try again

### "Domain not working"
- Make sure you've reserved the domain on Cpolar dashboard
- Domain registration is free but must be done first

### "404 Error"
- Check if your app is running (Step 4)
- Check if tunnel is active (Step 5)
- Wait 10-15 seconds after tunnel creation for DNS to propagate

## Next Time
Just run these two commands in separate PowerShell windows:

**Window 1:**
```powershell
cd C:\Code\csiph-attendance-main
dotnet run
```

**Window 2:**
```powershell
cd "C:\Program Files\cpolar"
.\cpolar.exe http 54169 -region cn -subdomain csi-attendance -daemon on
```

Done! Your app is now live on csi-attendance.cpolar.cn
