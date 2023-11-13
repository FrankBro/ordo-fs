module Repl

open Eval
open Expr
open Infer
open Parse

type Env = {
    Infer: Map<Name, Ty>
    Eval: Map<Name, Value>
}
with
    static member Empty = {
        Infer = Map.empty
        Eval = Map.empty
    }

let flushStr str = printf "%s" str

let readPrompt prompt = 
    flushStr prompt
    System.Console.ReadLine ()

let evalString (env: Env) expr : Env * string =
    try
        let ordoExpr = parse "repl" expr
        match ordoExpr with
        | EVariant ("rawtype", EVar name) ->
            env, Map.find name env.Infer |> string
        | EVariant ("type", EVar name) ->
            env, Map.find name env.Infer |> stringOfTy
        | EVariant ("value", EVar name) ->
            env, Map.find name env.Eval |> string
        | _ ->
            let ordoTy = 
                Infer.inferExpr Map.empty env.Infer 0 ordoExpr
                |> generalize
            let ordoVal = Eval.evalExpr Map.empty env.Eval ordoExpr
            let env =
                match ordoExpr with
                | ELet (EVar name1, _, EVar name2) when name1 = name2 ->
                    {
                        Infer = Map.add name1 ordoTy env.Infer
                        Eval = Map.add name1 ordoVal env.Eval
                    }
                | ELet (pat, _, ret) when pat = ret ->
                    let patTy, infered = inferPattern Map.empty env.Infer 0 pat
                    unify ordoTy patTy
                    let matches, evaled = evalPattern env.Eval pat ordoVal
                    {
                        Infer = infered
                        Eval = evaled
                    }
                | ESet (name, _, EVar ret) when name = ret ->
                    let patTy, infered = inferPattern Map.empty env.Infer 0 (EVar name)
                    unify ordoTy patTy
                    let matches, evaled = evalPattern env.Eval (EVar name) ordoVal
                    {
                        Infer = infered
                        Eval = evaled
                    }
                | _ ->
                    env
            env, stringOfValue ordoVal
    with 
    | e -> 
        env, (sprintf "Error: %A" e)

let evalAndPrint env expr : Env =
    let env, output = evalString env expr
    printfn "%s" output
    env

let rec until env pred prompt (action: Env -> string -> Env) =
    let result = prompt ()
    if pred result then
        ()
    else
        let env = action env result
        until env pred prompt action

let runRepl () =
    Infer.resetId ()
    let env = Env.Empty
    printfn "':type name' for type, type 'quit' to quit."
    until env ((=) "quit") (fun () -> readPrompt "ordo>>> ") evalAndPrint
