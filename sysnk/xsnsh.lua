local username, path
do
    local a, b = ...
    username = a or "user"
    path = b or { [1] = "/prog/sys", [2] = "/prog/usr" }
end

local clean_path = {}
for k, v in pairs(path) do
    clean_path[k] = v:gsub("/$", "")
end

local function prompt()
    local cwd = sys_cwd()
    io.write(string.format("xsnos:%s$%s > ", username, cwd))
end

local function parse_cmd(line)
    local parts = {}
    for token in line:gmatch("%S+") do
        parts[#parts+1] = token
    end
    if #parts == 0 then return nil end
    local ucmd = { cmd = parts[1] }
    for i = 2, #parts do
        ucmd["arg" .. (i - 2)] = parts[i]
    end
    return ucmd
end

local function file_exists(path)
    local f = io.open(path, "r")
    if f then f:close() return true end
    return false
end

local function find_cmd(cmd)
    if cmd:match("^/") or cmd:match("^%.") then
        local p = cmd
        if not p:match("%.lua$") then p = p .. ".lua" end
        if file_exists(p) then
            local chunk, err = loadfile(p)
            return p, chunk, err
        end
        return nil, nil, "file not found: " .. p
    end

    for _, dir in pairs(clean_path) do
        local p = dir .. "/" .. cmd .. ".lua"
        if file_exists(p) then
            local chunk, err = loadfile(p)
            return p, chunk, err
        end
    end
    return nil, nil, nil
end

local builtins = {}

builtins["extsh"] = function(_)
    print("cya.")
    os.exit(0)
end

sys_clear()
print(string.format("XSNOS2 26.10, logged in as %s", username))
print("type 'extsh' to quit.")
print()

while true do
    prompt()
    local line = io.read("l")

    if not line then
        print()
        break
    end

    line = line:match("^%s*(.-)%s*$")
    if line == "" then
    elseif builtins[line:match("^(%S+)")] then
        local ucmd = parse_cmd(line)
        builtins[ucmd.cmd](ucmd)
    else
        local ucmd = parse_cmd(line)
        if ucmd then
            local fullpath, chunk, err = find_cmd(ucmd.cmd)
            if not fullpath then
                print("E: file non-existant or not a lua file")
            elseif not chunk then
                print("E: " .. tostring(err))
            else
                local ok, runerr = pcall(chunk, ucmd)
                if not ok then
                    print("E: " .. tostring(runerr))
                end
            end
        end
    end
end