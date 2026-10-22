local function file_exists(path)
    local dir = path:match("^(.*)/[^/]*$") or "/"
    local name = path:match("[^/]+$")
    local entries = sys_listdir(dir)
    if not entries then return false end
    for _, e in ipairs(entries) do
        if e.name == name and e.kind == "file" then return true end
    end
    return false
end

local function parse_ini(path)
    local f = io.open(path, "r")
    if not f then return nil end
    local result = {}
    local section = nil
    for raw in f:lines() do
        local line = raw:match("^%s*(.-)%s*$")
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
    print("welcome to xsnos2, make a user.")
    print(" ")

    local username = input("Username: ")
    local password = input("Password: ")

    sys_mkdir("/sys")
    write_file(pass_path, string.format("[%s]\npassword=%s\n", username, password))
    have_pass = true

    print("user made, have fun.")
end

local path = {}
local reg = parse_ini(reg_path)

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
    local keys = {}
    for k, _ in pairs(reg["PATH"]) do
        if tonumber(k) then keys[#keys+1] = tonumber(k) end
    end
    table.sort(keys)
    for _, k in ipairs(keys) do
        path[k] = reg["PATH"][tostring(k)]
    end
else
    write_file(reg_path, "[PATH]\n1=/prog/sys\n2=/prog/usr\n")
    path[1] = "/prog/sys"
    path[2] = "/prog/usr"
end

local logged_in_user = nil

if have_pass then
    sys_clear()
    print("XSNOS2 26.10")
    print(" ")
    local pass_data = parse_ini(pass_path)
    for attempt = 1, 3 do
        local uname = input("Username: ")
        local upass = input("Password: ")
        if pass_data and pass_data[uname] and pass_data[uname]["password"] == upass then
            logged_in_user = uname
            break
        else
            print("invalid credentials.")
        end
    end
    if not logged_in_user then
        print("too many failed attempts. no more xsnos2 for you.")
        while true do sys_readkey() end
    end
end

sys_clear()

local shell, err = loadfile("/sys/xsnsh.lua")
if not shell then
    print("E: could not load shell: " .. tostring(err))
    while true do sys_readkey() end
end

shell(logged_in_user, path)