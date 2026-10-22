-- shell.lua
-- xsnos2 shell
-- called by init as: shell(username, path)

local username = arg[0] or "user"
local path     = arg[1] or { [1] = "/prog/sys", [2] = "/prog/usr" }

-- if loaded via shell(user, path) from init, grab the real args
-- loadfile + call passes varargs, so:
do
    local a, b = ...
    if a then username = a end
    if b then path = b end
end

-- ── helpers ───────────────────────────────────────────────────────────────────

-- strip trailing slash from path entries once
local clean_path = {}
for k, v in pairs(path) do
    clean_path[k] = v:gsub("/$", "")
end

local function prompt()
    local cwd = sys_cwd()
    io.write(string.format("xsnos:%s$%s > ", username, cwd))
end

-- parse a raw input line into ucmd table
-- "foo arg1 arg2 arg3" -> { cmd="foo", arg0="arg1", arg1="arg2", ... }
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

-- search path for cmd, return full filepath or nil
local function find_cmd(cmd)
    for _, dir in pairs(clean_path) do
        local fullpath = dir .. "/" .. cmd .. ".lua"
        -- try to loadfile; if it returns a function the file exists
        local chunk, err = loadfile(fullpath)
        if chunk then return fullpath, chunk end
    end
    return nil, nil
end

-- ── built-ins ─────────────────────────────────────────────────────────────────

local builtins = {}

builtins["cd"] = function(ucmd)
    local target = ucmd["arg0"] or "/"
    sys_chdir(target)
end

builtins["exit"] = function(_)
    print("Goodbye.")
    os.exit(0)
end

-- ── main loop ─────────────────────────────────────────────────────────────────

sys_clear()
print(string.format("XSNOS2 shell  |  logged in as %s", username))
print("Type 'exit' to quit.")
print()

while true do
    prompt()
    local line = io.read("l")

    -- handle nil (EOF)
    if not line then
        print()
        break
    end

    line = line:match("^%s*(.-)%s*$") -- trim
    if line == "" then
        -- nothing typed, loop
    elseif builtins[line:match("^(%S+)")] then
        local ucmd = parse_cmd(line)
        builtins[ucmd.cmd](ucmd)
    else
        local ucmd = parse_cmd(line)
        if ucmd then
            local fullpath, chunk = find_cmd(ucmd.cmd)
            if not fullpath then
                print("E: file non-existant or not a lua file")
            else
                -- pass ucmd as the first argument to the program
                local ok, err = pcall(chunk, ucmd)
                if not ok then
                    print("E: " .. tostring(err))
                end
            end
        end
    end
end
