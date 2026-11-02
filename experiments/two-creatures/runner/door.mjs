// The judge's door: a TCP forwarder the runner starts in a container on Docker's default bridge, joined to the sandbox
// network too, so a judge on the host reaches world's app (8080) and deployer (7001) through an address the host can
// answer. The host cannot talk to the sandbox's own addresses: they are in the mesh's range (the hub wants that), and
// Tailscale drops input from that range on any interface but its own. TARGET is world's sandbox address.
import net from "node:net";

const target = process.env.TARGET ?? "100.127.200.10";
for (const port of (process.env.PORTS ?? "8080,7001").split(",").map(Number)) {
  net.createServer((client) => {
    const upstream = net.connect(port, target);
    client.pipe(upstream).pipe(client);
    const drop = () => { client.destroy(); upstream.destroy(); };
    client.on("error", drop);
    upstream.on("error", drop);
  }).listen(port, "0.0.0.0", () => console.log(`door: ${port} -> ${target}:${port}`));
}
