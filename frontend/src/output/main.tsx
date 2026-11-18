import { createRoot } from "react-dom/client";
import { OutputView } from "./OutputView";
import "./output.css";

const root = document.getElementById("root");
if (!root) throw new Error("Falta #root");

createRoot(root).render(<OutputView />);
