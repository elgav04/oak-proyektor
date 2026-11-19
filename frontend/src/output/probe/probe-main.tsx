import { createRoot } from "react-dom/client";
import { OutputProbe } from "./OutputProbe";

const root = document.getElementById("root");
if (!root) throw new Error("Falta #root");

createRoot(root).render(<OutputProbe />);
