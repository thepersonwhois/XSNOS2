local devices = sys_lsdev()
for index, name in ipairs(devices) do
    print(string.format("[%d] %s", index - 1, name))
end