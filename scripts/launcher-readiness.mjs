import { createConnection } from "node:net";

export async function isUnixSocketAcceptingConnections(socketPath, timeoutMs = 1_000) {
  return new Promise((resolve) => {
    const socket = createConnection({ path: socketPath });
    let settled = false;
    const finish = (isReady) => {
      if (settled) return;
      settled = true;
      socket.destroy();
      resolve(isReady);
    };

    socket.setTimeout(timeoutMs);
    socket.once("connect", () => finish(true));
    socket.once("error", () => finish(false));
    socket.once("timeout", () => finish(false));
  });
}
