module Eval

open Error
open Expr
open Util

let rec evalExpr files (env: Map<string, Value>) (expr: Expr) : Value =
    match expr with
    | EFor (key, value, target, body, rest) -> failwith "EFor not supported"
    // | EFile filename -> failwith "EFile not supported"
    | ESet (name, value, body) ->
        if not (Map.containsKey name env) then
            raise (genericError (VariableNotFound name))
        let env =
            env
            |> Map.add name (evalExpr files env value)
        evalExpr files env body
    | EError s ->
        failwith s
    | EPrint (e, rest) ->
        printfn "%O" (evalExpr files env e)
        evalExpr files env rest
    // | EType (e, _) -> evalExpr files env e
    | EOpen filename -> Map.find filename files
    | EListEmpty -> VList []
    | EListCons (x, xs) ->
        let xsValue = evalExpr files env xs
        match xsValue with
        | VList xs ->
            let xValue = evalExpr files env x
            VList (xValue :: xs)
        | _ ->
            raise (evalError InvalidList)
    | EFix name ->
        let fnValue =
            Map.tryFind name env
            |> Option.defaultWith (fun () ->
                raise (genericError (VariableNotFound name))
            )
        match fnValue with
        | VFun (innerEnv, EVar fnName, (EFun(arg, rest) as fn)) ->
            let fnValue = evalExpr files innerEnv fn
            let env = Map.add fnName fnValue innerEnv
            VFun (env, arg, rest)
            // evalExpr env fn
        | _ -> raise (genericError (InvalidFix name))
    | EBool b -> VBool b
    | EInt i -> VInt i
    | EFloat f -> VFloat f
    | EString s -> VString s
    | EFun (pattern, expr) -> VFun (env, pattern, expr)
    | EVar name -> 
        Map.tryFind name env
        |> Option.defaultWith (fun () ->
            raise (genericError (VariableNotFound name))
        )
    | ECall (fnExpr, argExpr) -> 
        let fnValue = evalExpr files env fnExpr
        match fnValue with
        | VFun (innerEnv, pattern, bodyExpr) ->
            let mergedEnv = Map.merge env innerEnv
            let argValue = evalExpr files mergedEnv argExpr
            let matches, fnEnv = evalPattern mergedEnv pattern argValue
            if not matches then
                raise (genericError (InvalidPattern pattern))
            evalExpr files fnEnv bodyExpr
        | _ -> raise (evalError (NotAFunction fnExpr))
    | ELet (pattern, valueExpr, bodyExpr) ->
        let value = evalExpr files env valueExpr
        let matches, env = evalPattern env pattern value
        if not matches then
            raise (genericError (InvalidPattern pattern))
        evalExpr files env bodyExpr
    | ERecordEmpty  -> 
        VRecord Map.empty
    | EVariant (label, expr) ->
        let value = evalExpr files env expr
        VVariant (label, value)
    | ERecordExtend (name, valueExpr, recordExpr) ->
        let recordValue = evalExpr files env recordExpr
        match recordValue with
        | VRecord fields ->
            if Map.containsKey name fields then
                raise (inferError (RowConstraintFail name))
            let valueValue = evalExpr files env valueExpr
            fields
            |> Map.add name valueValue
            |> VRecord
        | _ -> raise (genericError (NotARecordExpr recordExpr))
    | ERecordRestrict (recordExpr, name) ->
        let recordValue = evalExpr files env recordExpr
        match recordValue with
        | VRecord fields ->
            fields
            |> Map.remove name
            |> VRecord
        | _ -> raise (genericError (NotARecordExpr recordExpr))
    | ERecordSelect (recordExpr, label) -> 
        let recordValue = evalExpr files env recordExpr
        match recordValue with
        | VRecord fields ->
            fields
            |> Map.tryFind label
            |> Option.defaultWith (fun () ->
                raise (genericError (FieldNotFound label))
            )
        | _ -> raise (genericError (NotARecordExpr recordExpr))
    | ECase (valueExpr, cases, oDefault) ->
        let valueValue = evalExpr files env valueExpr
        let oCases = tryMakeVariantCases cases
        match valueValue, oCases with
        | VVariant (label, value), Some cases ->
            let pattern, expr =
                cases
                |> List.tryFind (fun (caseLabel, pattern, _, oGuard) -> 
                    let patternMatches, _ = evalPattern env pattern value
                    let isGuardTrue () =
                        match oGuard with
                        | None -> true
                        | Some guard ->
                            let matches, guardEnv = evalPattern env pattern value
                            match evalExpr files guardEnv guard with
                            | VBool value -> value && matches
                            | _ -> raise (genericError (InvalidGuard guard))
                    caseLabel = label && isGuardTrue () && patternMatches
                )
                |> Option.map (fun (_, pattern, expr, _) -> pattern, expr)
                |> Option.defaultWith (fun () ->
                    oDefault
                    |> Option.map (fun (name, expr) -> EVar name, expr)
                    |> Option.defaultWith (fun () ->
                        raise (evalError (MissingMatchCase valueExpr))
                    )
                )
            let matches, fnEnv = evalPattern env pattern value
            if not matches then
                raise (genericError (InvalidPattern pattern))
            evalExpr files fnEnv expr
        | _ -> 
            let value = evalExpr files env valueExpr
            let pattern, expr =
                cases
                |> List.tryFind (fun (pattern, expr, oGuard) ->
                    let patternMatches, _ = evalPattern env pattern value
                    let isGuardTrue () =
                        match oGuard with
                        | None -> true
                        | Some guard ->
                            let matches, guardEnv = evalPattern env pattern value
                            match evalExpr files guardEnv guard with
                            | VBool value -> value && matches
                            | _ -> raise (genericError (InvalidGuard guard))
                    isGuardTrue () && patternMatches
                )
                |> Option.map (fun (pattern, expr, _) -> pattern, expr)
                |> Option.defaultWith (fun () ->
                    oDefault
                    |> Option.map (fun (name, expr) -> EVar name, expr)
                    |> Option.defaultWith (fun () ->
                        raise (evalError (MissingMatchCase valueExpr))
                    )
                )
            let matches, fnEnv = evalPattern env pattern value
            if not matches then
                raise (genericError (InvalidPattern pattern))
            evalExpr files fnEnv expr
    | EIfThenElse (ifExpr, thenExpr, elseExpr) ->
        let ifValue = evalExpr files env ifExpr
        match ifValue with
        | VBool true -> evalExpr files env thenExpr
        | VBool false -> evalExpr files env elseExpr
        | _ -> 
            raise (genericError IfValueNotBoolean )
    | EUnOp (op, a) ->
        let a = evalExpr files env a
        match op, a with
        | Negative, VInt a -> VInt (-a)
        | Negative, VFloat a -> VFloat (-a)
        | _ -> raise (evalError BadUnOp)
    | EBinOp (a, op, b) ->
        let a = evalExpr files env a
        match op with
        | And ->
            match a with
            | VBool true ->
                let b = evalExpr files env b
                match b with
                | VBool b -> VBool b
                | _ -> 
                    raise (evalError BadBinOp)
            | VBool false -> VBool false
            | _ -> 
                raise (evalError BadBinOp)
        | Or ->
            match a with
            | VBool false ->
                let b = evalExpr files env b
                match b with
                | VBool b -> VBool b
                | _ -> 
                    raise (evalError BadBinOp)
            | VBool true -> VBool true
            | _ -> 
                raise (evalError BadBinOp)
        | _ ->
            let b = evalExpr files env b
            match a, op, b with
            | VInt a, Plus, VInt b -> VInt (a + b)
            | VFloat a, Plus, VFloat b -> VFloat (a + b)
            | VInt a, Minus, VInt b -> VInt (a - b)
            | VFloat a, Minus, VFloat b -> VFloat (a - b)
            | VInt a, Multiply, VInt b -> VInt (a * b)
            | VFloat a, Multiply, VFloat b -> VFloat (a * b)
            | VInt a, Divide, VInt b -> VInt (a / b)
            | VFloat a, Divide, VFloat b -> VFloat (a / b)
            | VBool a, Equal, VBool b -> VBool (a = b)
            | VInt a, Equal, VInt b -> VBool (a = b)
            | VFloat a, Equal, VFloat b -> VBool (a = b)
            | VRecord a, Equal, VRecord b -> VBool (a = b)
            | VVariant (name1, value1), Equal, VVariant (name2, value2) -> VBool (name1 = name2 && value1 = value2)
            | VBool a, NotEqual, VBool b -> VBool (a <> b)
            | VInt a, NotEqual, VInt b -> VBool (a <> b)
            | VFloat a, NotEqual, VFloat b -> VBool (a <> b)
            | VRecord a, NotEqual, VRecord b -> VBool (a = b)
            | VVariant (name1, value1), NotEqual, VVariant (name2, value2) -> VBool (name1 = name2 && value1 = value2)
            | VInt a, Greater, VInt b -> VBool (a > b)
            | VFloat a, Greater, VFloat b -> VBool (a > b)
            | VInt a, GreaterEqual, VInt b -> VBool (a >= b)
            | VFloat a, GreaterEqual, VFloat b -> VBool (a >= b)
            | VInt a, Lesser, VInt b -> VBool (a < b)
            | VFloat a, Lesser, VFloat b -> VBool (a < b)
            | VInt a, LesserEqual, VInt b -> VBool (a <= b)
            | VFloat a, LesserEqual, VFloat b -> VBool (a <= b)
            | _ -> 
                raise (evalError BadBinOp)

and evalPattern (env: Map<string, Value>) pattern (value: Value) : bool * Map<_, _> =
    let initialEnv = env
    let rec loop env pattern (value: Value) =
        match pattern with
        // | EType (e, _) -> loop env e value
        | EListEmpty -> VList [] = value, env
        | EListCons (x, xs) ->
            match value with
            | VList [] ->
                false, env
            | VList (xValue :: xsValue) ->
                let xValid, env = loop env x xValue
                let xsValid, env = loop env xs (VList xsValue)
                xValid && xsValid, env
            | _ ->
                raise (evalError InvalidList)
        | EBool b -> VBool b = value, env
        | EInt i -> VInt i = value, env
        | EFloat f -> VFloat f = value, env
        | EString s -> VString s = value, env
        | EVar var -> true, Map.add var value env
        | ERecordEmpty -> true, env
        | ERecordExtend (label, expr, record) ->
            match value with
            | VRecord fields ->
                let field = 
                    fields
                    |> Map.tryFind label
                    |> Option.defaultWith (fun () ->
                        raise (genericError (FieldNotFound label))
                    )
                let matches, env = evalPattern env expr field
                if not matches then
                    false, initialEnv
                else
                    let remainingFields =
                        fields
                        |> Map.remove label
                    loop env record (VRecord remainingFields)
            | _ ->
                raise (genericError (NotARecordValue value))
        | EVariant (label, expr) ->
            match value with
            | VVariant (name, value) when label = name ->
                let matches, env = evalPattern env expr value
                matches, env
            | VVariant (name, _) ->
                raise (evalError (BadVariantPattern (label, name)))
            | _ ->
                raise (genericError (NotAVariantValue value))
        | _ -> 
            raise (genericError (InvalidPattern pattern))
    loop env pattern value

let eval files expr : Value =
    evalExpr files Map.empty expr
