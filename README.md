<h1>XSNOS 2 (XSNOS NX)</h1>
<p>XSNOS 2 is an operating system that is definitely not very poorly made.</p>

<h2>building</h2>
<p>go into project root and run <code>dotnet publish -c Debug -r linux-x64 -o output-x64 --verbosity minimal</code></p>

<h2>usage</h2>
<p>type hlpcm for help.</p>

<h2>making userspace programs</h2>
<p>use txted. It's just lua 5.5. assume anything not listed below in not working works, unless proven otherwise. file an issue if something doesn't work.</p>
<p>The non-stock lua syscalls are</p>
<ul>
  <li>sys_green() - makes your text GREEN, unfortunately not medium spring.</li>
  <li>sys_scrh() - gets the screen height</li>
  <li>sys_scrw() - gets the screen width</li>
  <li>sys_ver() - gets the version of xsnos2 you're on. i think this is 26.10.</li>
  <li>sys_white() - undoes sys_green(), as sys_green() is permanent and persists when your lua program closes, but why would you ever want to undo sys_green()</li>
  <li>sys_cwd() - gets the cwd because we need that</li>
  <li>sys_readkey() - reads your keys. returns key name, character (with some exceptions, like unprintable keys), and a boolean for if ctrl is held</li>
  <li>sys_goto(x,y) - this is not goto. it just flings your cursor to point x,y.</li>
  <li>sys_putat(x,y,text,cursorX,cursorY) - writes text at a position, then sends your cursor somewhere. like goto but more chris pratt-like</li>
  <li>sys_clear() - clear</li>
  <li>sys_cursor(visible) - gee i wonder it totally doesnt invisible the cursor</li>
  <li>sys_listdir(path) - lists. returns a table of {name, kind, size} entries. kind is file or dir.</li>
  <li>sys_mkdir(path), sys_chdir(path), sys_remove(path, recursive), sys_copy(source, destination) - these do exactly what you think they do.</li>
  <li>sys_partitions() - lists parts as a table. {index, name, blocks, block_size, host}</li>
  <li>sys_mkfat(index, label, type) - makes fat.</li>
  <li>sys_prtinfo() - returns {name, block_count, block_size, scheme} for the primary disk. scheme is gpt, mbr, or raw (like republicans like their meat)</li>
  <li>sys_prtscheme(choice) - sets partition scheme. 1 gpt 2 mbr 3 keep same</li>
  <li>sys_partition(start,count) - make a partition. returns sucess,message</li>
  <li>sys_lsdev() - lists block devices. it's a table.</li>
  <li>sys_installer_disks() - returns a table of disks. {index, name, size_gib, is_boot}, returns nil if it can't tell if it's a boot disk or not</li>
  <li>sys_install(diskname) - does what it says on the tin, installs XSNOS2.</li>
</ul>
<p>the non-working lua functions are</p>
<ul>
  <li>io.popen()</li>
  <li>os.tmpname()</li>
  <li>os.getenv()</li>
</ul>
