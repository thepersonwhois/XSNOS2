local partitions = sys_partitions()
if #partitions == 0 then
    print("mkfat: no partitions found. run mkprt first.")
    return
end

print("available partitions:")
for _, partition in ipairs(partitions) do
    print(string.format("[%d] %s (%d blocks on %s)", partition.index, partition.name, partition.blocks, partition.host))
end

io.write("partition to format: ")
io.flush()
local index = tonumber(io.read("*l"))
if index == nil or index % 1 ~= 0 then
    print("mkfat: invalid selection.")
    return
end

io.write('volume label (max 11 chars, blank for "COSMOS"): ')
io.flush()
local label = io.read("*l") or ""
if label:match("^%s*$") then
    label = "COSMOS"
end
if #label > 11 then
    label = string.sub(label, 1, 11)
end

io.write("fat type [12/16/32, default 32]: ")
io.flush()
local requested_type = (io.read("*l") or ""):match("^%s*(.-)%s*$")
local fat_type = requested_type == "12" and 12 or requested_type == "16" and 16 or 32

io.write("formatting (this destroys everything on the partition)... ")
io.flush()
local ok, message = sys_mkfat(index, label, fat_type)
if not ok then
    print("failed: " .. tostring(message))
    return
end

print("done; " .. tostring(message))