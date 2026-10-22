local function file_exists(path)
    local entries = sys_listdir(path:match("^(.*)/[^/]*$") or "/")
    if not entries then return false end
    local name = path:match("[^/]+$")
    for _, e in ipairs(entries) do
        if e.name == name and e.kind == "file" then return true end
    end
    return false
end
local function parse_ini(path)
    -- read by loading as a chunk is not possible, so we use sys_listdir trick;
    -- instead we rely on the fact that loadfile can load lua, not ini.
    -- We'll open with io.open (stock lua 5.5, should work).
    local f = io.open(path, "r")
    if not f then return nil end
    local result = {}
    local section = nil
    for line in f:lines() do
        line = line:match("^%s*(.-)%s*$") -- trim
        if line:match("^%[.+%]$") then
            section = line:match("^%[(.+)%]$")
            result[section] = result[section] or {}
        elseif section and line:match("^[^;#=]+=") then
            local k, v = line:match("^(.-)%s*=%s*(.*)$")
            if k then result[section][k] = v end
        end
    end
    f:close()
    return result
end

local function write_file(path, contents)
    local f = io.open(path, "w")
    if not f then return false end
    f:write(contents)
    f:close()
    return true
end

local function input(prompt)
    io.write(prompt)
    return io.read("l")
end

local function mount_all()
    local devs = sys_lsdev()
    if not devs then return end
    for _, dev in ipairs(devs) do
        -- mount each device under /mnt/<devname>
        local mountpoint = "/mnt/" .. dev
        sys_mkdir(mountpoint)
        sys_mount(dev, mountpoint)
    end
end

mount_all()


local pass_path = "/sys/pass.ini"
local reg_path  = "/sys/reg.ini"

local have_pass = file_exists(pass_path)
local have_reg  = file_exists(reg_path)

if not have_pass or not have_reg then
    sys_clear()
    print("welcome to XSNOS2 26.10.")
    print("this is the first boot setup.")
    print("this will be transferred over after an install.")

    local username = input("Username: ")
    local password = input("Password: ")

    local pass_contents = string.format("[%s]\npassword=%s\n", username, password)
    sys_mkdir("/sys")
    write_file(pass_path, pass_contents)
    have_pass = true

    print("user created. welcome.")
end

local path = {}

local reg = parse_ini(reg_path)

-- check if [PATH] section has at least one numeric key
local path_ok = false
if reg and reg["PATH"] then
    for k, _ in pairs(reg["PATH"]) do
        if tonumber(k) then
            path_ok = true
            break
        end
    end
end

if path_ok then
    -- collect all numeric keys in order
    local keys = {}
    for k, _ in pairs(reg["PATH"]) do
        if tonumber(k) then keys[#keys+1] = tonumber(k) end
    end
    table.sort(keys)
    for _, k in ipairs(keys) do
        path[k] = reg["PATH"][tostring(k)]
    end
else
    -- write defaults
    local reg_contents = "[PATH]\n1=/prog/sys\n2=/prog/usr\n"
    write_file(reg_path, reg_contents)
    path[1] = "/prog/sys"
    path[2] = "/prog/usr"
end

local logged_in_user = nil

if have_pass then
    sys_clear()
    print("XSNOS2 Login")
    print("────────────")
    local pass_data = parse_ini(pass_path)
    for attempt = 1, 3 do
        local uname = input("Username: ")
        local upass = input("Password: ")
        if pass_data and pass_data[uname] and pass_data[uname]["password"] == upass then
            logged_in_user = uname
            break
        else
            print("Invalid credentials.")
        end
    end
    if not logged_in_user then
        print("Too many failed attempts. Halting.")
        while true do sys_readkey() end
    end
end

sys_clear()

local shell, err = loadfile("/prog/sys/xsnsh.lua")
if not shell then
    print("E: could not load shell: " .. tostring(err))
    while true do sys_readkey() end
end

shell(logged_in_user, path)
